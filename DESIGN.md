# CelestialLighting — Design

## Problem

"Tilt Planet! – Realism Overhaul" (Workshop 3520836521, delisted) had lighting the user liked —
axial-tilt-driven shadow direction, dramatic seasonal twilight — bundled with unrelated
economy/material changes. No code from it exists anywhere accessible; this mod is built purely
from public Workshop screenshots/description text plus decompiling *vanilla* `Assembly-CSharp.dll`
to understand RimWorld's existing celestial/sky systems. Scope is visual/atmospheric only — no
pawn work-speed or move-speed penalties.

Phase 1 covers exactly two effects: shadow direction and twilight color. (A third, a subtle
per-position shadow-length tilt across a single map, was built and later removed — see §3.) Two
follow-up fixes were folded in
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

**Handedness (resolved)**: `+X` is compass east, and the sun rises there. This sat open for a
while as "not verified from decompiled code alone", and it turned out we had it backwards the
whole time — `SolarAzimuthDegrees` was missing the leading minus that the standard
north-clockwise formula puts on `sin(Az) = -cos(δ)·sin(H)/cos(el)`. Since `HourAngleDegrees` is
negative before noon, dropping that minus put the morning sun at 270° and ran the sun east-to-west
backwards across every map. Nothing downstream flipped it back
(`ShadowVectorFromSunPosition` negates east and north together, `Patch_ShadowDirection` passes `X`
straight into `info.vector.x`), so the formula sign was the on-screen sign.

Vanilla settles the axis question independently, which is worth writing down because it was the
open half of this for months. `GenCelestial.GetLightSourceInfo`'s own daytime branch is
`num4 = Mathf.LerpUnclamped(-15f, 15f, dayPercent)`, assigned straight to `result.vector.x` — so
vanilla throws morning shadows toward **negative** X and afternoon shadows toward positive X. That
is RimWorld stating its own convention in code: `+X` is east and its sun rises there. Our fixed
azimuth now agrees with vanilla's sign at every hour; the pre-fix version disagreed with it all day.

Worth recording *how* it survived: the solar-geometry suite was thorough but every azimuth
assertion was symmetric about noon (`SolarAzimuth_MirrorsAcrossNoon`) or evaluated at noon itself,
where the sign cancels. A mirrored sky satisfies all of them. It took someone noticing the
shadows in a screenshot. The fix added `SolarAzimuth_SunRisesInTheEast` and
`ShadowVector_PointsWestAtSunriseAndEastAtSunset`, which are asymmetric by construction and can
only pass one way round — the general lesson being that a symmetry test never pins an orientation,
and orientation needs its own anchor.

Live-verified by `Tests/Scenarios/sun_handedness.json` (lat 55, equinox), run A/B against builds
differing only in that sign:

| clock | fixed | pre-fix | elevation |
|---|---|---|---|
| 08:00 | `shadow_vector_x = -2.2799` | `+2.2799` | 24.7819 (both) |
| 12:00 | `0` | `0` | 35.0000 (both) |
| 16:00 | `+2.2799` | `-2.2799` | 24.7819 (both) |

Elevation and noon are untouched, which is the point: the bug was purely azimuthal, so a suite that
only ever checked height or noon could not see it. Note the magnitudes are not what naive
`hour/24` solar geometry predicts (±4.2276) — §14's `SunClockAdapter.EffectiveDayPercent` warps the
day percent so our physical sun crosses the horizon when vanilla's sky does, compressing the hour
angle symmetrically about noon (08:00 lands at |H| = 43.05°, not 60°). Anyone recomputing these
pins by hand must apply that warp or they will "find" a discrepancy that isn't there.

`LightInfo.intensity` is written here even though nothing in vanilla reads it for
`LightType.Shadow` — decompiling `SkyManager` shows it consumed only on the `LightingSun` /
`LightingMoon` paths, and the shadow's visible opacity comes from `GenCelestial.CurShadowStrength`
instead (§5). It is set, and set consistently with `Patch_ShadowStrength`, so that any third-party
postfix on `GetLightSourceInfo` that does read it sees our answer rather than vanilla's
un-suppressed one. It used to have an in-mod consumer as well, in §3's removed shadow tilt.

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

Both factors take the shared `inVacuum` gate and return `0` on an Odyssey space map: twilight *is*
scattering, so with no air there is no twilight to shorten. See §18.

## 3. The sun-shadow shader — and the across-map tilt we removed (`Patch_ShadowMeshPerimeter`)

This section is now two things: the record of what RimWorld's shadow shader actually does, which
every other shadow feature we own leans on, and the post-mortem of the one effect built on top of
it that we later took back out.

The removed effect: shadows subtly longer on one side of the map than the other (a per-position
variant of effect 1, at colony-map scale rather than map-tile-of-the-world scale).

Vanilla has no mechanism for this: `SkyManager.SetSunShadowVector` sets the shadow vector via
`Shader.SetGlobalVector(ShaderPropertyIDs.MapSunLightDirection, ...)` — one value for the entire
map, applied uniformly by every section's `SunShadow`-shader draw call
(`MeshMakerShadows`/`SectionLayer_SunShadows` build only the shadow mesh's *footprint*; the actual
push/extrusion along the shadow vector happens in the shader itself, reading that one global).

### What the shipped shader actually declares (issue #11)

The compiled shader isn't visible from decompiled C#, but it *is* visible in
`RimWorldLinux_Data/resources.assets`, which was scanned directly to settle this. Findings:

- `MatBases.SunShadow`'s shader is **`Custom/Sun shadow`**, and it declares **no** `Properties {}`
  entry for `_CastVect`. The literal string `_CastVect` occurs five times in the whole asset file:
  twice inside the `SunShadow` / `SunShadowFade` materials' serialized `m_SavedProperties`, and three
  times inside compiled shader blobs, as a member of the `$Globals` constant buffer. It never occurs
  as a `SerializedProperty` name/description pair (the form real `Properties {}` entries take, e.g.
  `_SwayHead` + `Sway head`). So it is a plain program uniform fed by `Shader.SetGlobalVector`.
- The vertex program is, in effect,
  `position.xyz = in_COLOR0.www * _CastVect.xyz + in_POSITION0.xyz` — **`_CastVect.xyz` is the
  extrusion vector**, direction and length together, **scaled per vertex by the mesh's own alpha**.
  That alpha is the knob this subsystem now uses; see the next subsection.
- **`_CastVect.w` is read by nothing.** The fragment program emits `_Color`. This is the whole reason
  the earlier live A/B (`Tests/Scenarios/penumbra_lowsun`), which folded shadow opacity into a
  per-section `_CastVect.w`, produced zero visible change — opacity lives in the material colour
  `SkyManager` lerps, so `.w` was always a dead channel. Opacity lives in `Patch_ShadowStrength` now,
  which is correct.
- **`_PenumbraSoftness` does not appear anywhere in the game's assets.** The `MaterialPropertyBlock`
  float that used to be pushed under that name was provably a silent no-op and has been deleted.

### Removed: the across-map length gradient (issues #11, #26)

**What it was.** Each section's vertex alpha was multiplied by a position-dependent factor so that
shadows grew ~15% longer toward the map edge the shadows point at and ~15% shorter at the opposite
edge — a parallax cue that the Sun is not infinitely distant. Because alpha is an unsigned byte and
vanilla already spends all 255 of it on a height-1.0 wall, the ramp could not simply be added on
top; it was re-anchored by dividing through by `1 + maxVariation`, putting the *far* edge at exactly
1.0 and shortening everything else away from it. That preserved the far/near ratio, which was the
whole visible effect, at the cost of every section-cast shadow sitting shorter than the pawn and
item shadows drawn straight from the global vector.

**Why it went.** Three reasons, in the order they became clear:

- **Nobody could see it.** ±15% across a 250-cell map is under a cell of difference between
  neighbouring sections, with no on-screen reference to compare against, and the user's own "Shadow
  length" slider moves shadows far further than the whole ramp did. It was never described in
  `About.xml` and never had a settings knob.
- **It was the mod's only self-scheduled section churn, and that dominated a live profile.** The
  baked value goes stale as the shadow axis rotates, so `MapComponent_SunShadowAxis` compared the
  live axis against the baked one every 15 ticks and raised a private `MapMeshFlagDef` once it had
  drifted past 0.5° — roughly 720 whole-map rebakes per game day, ~0.7/s at normal speed and ~2.2/s
  at 3×. Per section that is only ~3.4 µs of bake on top of a 26 µs regenerate, and amortised it is
  only 0.012–0.037 ms/frame, which is why the per-section ledger in §16 ranks it well below §9 and
  §7b. But every *other* section layer's cost is paid only when something in that section actually
  changes, which in a settled colony is rare. This was the one feature dirtying sections on a clock,
  forever, and Dubs Performance Analyzer captures it accordingly.
- **It could not be made correct without re-growing the patch surface.** `WeatherEvent` and
  `CompAffectsSky` override the shadow vector *downstream* of `GenCelestial.GetLightSourceInfo`, so
  while one was active the baked alpha stayed anchored to the un-overridden azimuth (issue #26).
  Fixing that meant a new patch on `SkyManager` to observe the post-override vector — precisely the
  hot-path patch the previous round of work had just deleted. Closed wontfix.

**What removing it changes on screen.** Every section-cast shadow is now `1.15 / (1 + 0.15 · f)`
longer than before, where `f` is the section's position fraction: ×1.00 at the far map edge, ×1.15
at map centre, ×1.35 at the near edge. Section shadows now agree exactly with pawn and item shadows
(`MeshMakerShadows`/`Printer_Shadow`), which always read the global vector unscaled. The old design's
uniform-sounding "13% shorter" was really a position-dependent 0–26% disagreement with them.

**What we kept from it.**

- `Formulas.ShadowCasterAlphaByte` survives, single-argument. Vanilla's own
  `(byte)(255f * staticSunShadowHeight)` is an *unchecked* cast over an unvalidated `ThingDef`
  float: a modded def declaring 1.2 wraps 306 → 50 and collapses that building's shadow to a stub.
  Vanilla's own defs never exceed 1.0, but `Patch_ShadowMeshPerimeter` replaces `Regenerate()` for
  the whole load order, so the clamp is ours to provide. It also rounds rather than truncates.
- **The quantization finding**, which is what issue #11 actually asked us to check rather than
  assume: one alpha level is 1/255 of the *global* extrusion vector, so its error in world units is
  `|_CastVect| / 255` regardless of caster height — short casters do not band worse than tall ones,
  they simply have fewer levels to spend on a proportionally shorter shadow. At
  `Formulas.MaxShadowLength` (15 cells) that is 0.059 cells, roughly one screen pixel at default
  zoom.
- **Two post-mortems worth more than the feature was.** The first implementation, `Patch_ShadowTilt`,
  pushed a rescaled `_CastVect` through a per-draw `MaterialPropertyBlock` from a Prefix on
  `SectionLayer_SunShadows.DrawLayer()`. The first live profiler capture (issue #23) put it at
  **0.300 ms/frame average, 1.590 ms max, 8.71 µs per call, 7.73% of a 3.879 ms frame** — about two
  thirds of the mod's entire 0.456 ms — *before* counting the draw batching it broke for 6–34
  sections, and it depended on a `MaterialPropertyBlock` being able to override a `$Globals` uniform
  the shader never declares, which the scan above showed it cannot. Second: the staleness check
  originally used a signed `current - last >= interval` guard, and a live scenario that jumped the
  clock from 17:00 back to 07:00 measured the gradient still baked against the *afternoon* axis,
  because ticks are not monotonic — saves, dev-mode clock tools and the harness's own `SetTime` all
  move them backwards. Any future throttle keyed on tick deltas wants the absolute difference.
- **The live guard.** `Tests/Scenarios/shadow_gradient_inertness.json` + `ShadowExtrusionProbe` are
  retained with their sense inverted: they read the baked alpha at both ends of the shadow axis and
  now require a far/near ratio of **1.00 ± 0.02**. That pins the removal (no gradient has come back)
  and simultaneously proves the bake path still runs, since a dead path reports a sentinel rather
  than two plausible cell counts.

**What went with it.** `MapComponent_SunShadowAxis` (reduced to an inert tombstone — see below),
`Patch_SunShadowAxisInvalidation`, `CelestialLightingDefOf`, and
`1.6/Defs/MapMeshFlagDefs/MapMeshFlags_CelestialLighting.xml`. That was the mod's only `MapComponent`,
its only Harmony patch on the `SectionLayer_SunShadows` constructor besides §15's, its only `DefOf`,
and its only def of any kind. The mod now ships no XML content at all and raises no map-mesh flag on
a schedule: every regeneration it causes is downstream of a vanilla edit.

**The save-compat hazard, and why a tombstone class remains.** `Verse.Map.ExposeComponents` scribes
its component list with `Scribe_Collections.Look(ref components, "components", LookMode.Deep, this)`,
so every save ever written with an older build carries a
`<li Class="CelestialLighting.MapComponent_SunShadowAxis" />` node per map. Deleting the type does
not silently drop that node: `ScribeExtractor.SaveableFromNode` fails to resolve the class, asks
`GetBestFallbackType` for a substitute (which returns `null` — `MapComponent` matches none of its
`Thing`/`Hediff`/`Ability` branches), logs `Could not find class ...`, then dereferences that null on
`type.IsAbstract`, throws, and logs a second error from its own catch. Two red errors per map.
`Map.FillComponents` then strips the null and the next save omits it, so it is one-time and
self-healing — but it is visible, and this mod is published, so the class survives as a dozen inert
lines with a dated removal note. It is safe to delete one release after this one, by which point
every affected save has been re-saved. Worth remembering for any future component: **removing a
`MapComponent` is a save-format change, not a code-only one.**

### Angular-size penumbra — softening shadow edges near the horizon (`PenumbraMath`)

Sections 1 and 4 treat the Sun as a point source, so shadows keep a perfectly hard edge and only their
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
  `1 − MaxContrastLoss` = 0.4 near the horizon) multiplies shadow opacity in `Patch_ShadowStrength`
  (`GenCelestial.CurShadowStrength`), the value `SkyManager` lerps `MatBases.SunShadow.color` by. A
  wider penumbra means a larger partially-shaded fraction of the footprint, i.e. a lower-contrast,
  washed-out shadow — approximated in the opacity channel the sun-shadow shader already reads,
  needing no shader property. Outright disappearance at the horizon stays
  `ShadowIntensityFromElevation`'s job; the two compose by multiplication. Elevation comes from the
  shared `SolarPosition.ElevationForMap`, so this reads the exact same Sun position as
  `Patch_ShadowDirection`/`Patch_ShadowStrength`.
- **No geometric edge blur, and no hook for one.** `PenumbraSoftness` used to also be pushed into a
  `_PenumbraSoftness` `MaterialPropertyBlock` float in the since-deleted per-draw tilt patch, as a
  forward hook for a
  shader that might one day declare such a uniform. Issue #11 settled that by scanning
  `resources.assets`: the string `_PenumbraSoftness` appears nowhere in the game, so the push was a
  guaranteed silent no-op and has been removed. `PenumbraSoftness` remains as the dimensionless form
  of the physics — `PenumbraContrastFactor` is a fixed function of it, and `PenumbraProbe` pins it
  live — but nothing pushes it at a shader. A true mesh-edge blur would need a shipped custom shader,
  which is ruled out.

Conflict risk: none of its own — the contrast factor rides `Patch_ShadowStrength`'s existing
`CurShadowStrength` Postfix and touches no new vanilla member (so no `ApiCompatibilityTests`
addition is needed). Clean-room: solar angular diameter and
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
lookup for `SectionLayer.section` (a protected field with no public accessor). It stays its own file,
despite having only this one caller, so the next patch needing a `Section` reuses it rather than
reinventing the `FieldRef`.

Since then the per-cell `Building` lookups have been routed through `EaveShadowGrid`, which resolves
an effective caster *height* per cell rather than an edifice, so §15's eaves can cast a roofline
shadow. With that feature off the two are provably identical; see §15 for why, and for why replacing
this whole method makes Perspective: Eaves incompatible.

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
  the moon's own strength keeps scaling the result — a weaker alpha reads at proportionally less
  contrast with no second curve to keep in sync — and the weather-event branch that skips the lerp and
  uses `colors.shadow` directly stays correct too. (What *sets* that alpha changed in §6b below: it is
  now a brightness ratio rather than a phase-and-altitude ramp, so "a half-lit moon" no longer means
  "half the alpha".)
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

### 6b. Moon shadows fade in on a brightness ratio (`IlluminanceMath`)

**Problem.** §6 and §6a between them computed a moon shadow and made it visible, but neither ever asked
whether the sky was dark enough for one. "Is there a moon shadow" was a hard branch on the sun being
below `Formulas.AtmosphericRefractionDegrees`, and the strength that followed was
`ramp(moonElevation) · illuminatedFraction · MoonShadowMaxStrength` — a fact about the moon alone.

Two things fell out of that, both visible in game:

1. **The shadow switched on at sunset, at strength.** The gate opens at sun elevation −0.83°, where the
   sky is still ~200 lux — roughly **750× brighter than a full moon**. The moon shadow appeared, at
   whatever its altitude implied, over a ramp only 3° of moon elevation wide. `moon_sun_clock.json`
   pinned that pop at hour 21.1 as a render of `0.801`, and §14's clock work had to spend a whole
   subsystem's worth of argument bounding the resulting step rather than removing it.
2. **Phase scaled the shadow linearly.** A half-lit moon cast exactly half the contrast, which is not
   how either photometry or perception works.

The underlying mistake is a units one, and it is worth stating plainly because it is the kind that
hides for a long time. Every other brightness quantity in this mod is a normalized 0–1 scalar — sky
glow, moonlight brightness, cloud opacity — which is fine while each subsystem only compares a value
to itself, and useless the moment two *different* light sources have to be compared to each other.
0–1 throws away the four orders of magnitude between sunlight and moonlight, so in those units the
question "can this shadow be seen" has no answer and a gate is the only thing left to write.

**Approach.** A new pure core, `IlluminanceMath`, works in absolute lux, and moon-shadow strength
becomes the contrast the two sources actually produce:

```
ambientLux  = AmbientSkyLux(sunElevation)        // log-interpolated twilight curve
moonLux     = FullMoonZenithLux · phase^3.5 · sin(moonElevation)
strength    = moonLux / (moonLux + ambientLux) · MoonShadowMaxStrength
```

Shadowed ground receives the ambient; lit ground receives ambient + moon. Their ratio is the whole
physical content of the subsystem, and it has the limits you want for free — approaching 1 when the
moon dominates, 0 when it is drowned — with no threshold deciding where that happens.

- **The sun's elevation is an input, not a gate.** Daylight washout is now something the model
  *derives* (a midday full-moon shadow computes to ~0.000003 contrast) rather than something a branch
  asserts. The sunset pop is gone because there is no longer a moment where anything switches: the
  sun's own shadow ramp has reached 0 by the handover, and the moon's is still 0 for several degrees
  after it. Measured on the shipped tile, a full moon's shadow first becomes perceptible around sun
  **−6.5°** and saturates around **−11°**; a quarter moon, ~11× fainter, arrives near **−9°** and
  saturates near **−15°**.
- **Anchors are published photometry, log-interpolated.** 120,000 lux at zenith, 400 at the horizon,
  3.4 at the end of civil twilight, 0.008 at nautical, 0.001 (starlight + airglow) at astronomical and
  below, where it clamps. Twilight falls roughly a decade per 3–4°, so linear interpolation would put
  the −3° sky at ~200 lux instead of ~37 and push the fade several degrees late.
- **Phase is a power law, not a fraction.** A first-quarter moon is ~1/12 as bright as a full one, not
  1/2 — the opposition surge plus shadowing on lunar relief. The exponent is derived from the
  published magnitudes (−12.74 full vs −10.0 first quarter → 2.74 mag → 12.4×; `0.5^k = 1/12.4` gives
  k ≈ 3.63, rounded to **3.5**), not tuned to taste.
- **A deliberate behaviour change, pinned by a test.** Phase no longer dims the shadow proportionally
  in full darkness: a half-lit moon now reads ~0.98 of a full moon's contrast rather than 0.50. That
  is the correct physics — contrast is a ratio, and any caster well above the starlight floor casts a
  near-full-contrast shadow. What a half moon actually costs is scene *brightness*, which is §7's to
  own. Phase still matters at both ends: it sets when the shadow arrives during twilight, and a
  ≤5%-lit crescent is genuinely dimmer than starlight and casts nothing.
- **`MoonShadowMaxStrength` (0.28) survives as a look knob.** Physically a full moon in true darkness
  is as dominant as the noon sun and its contrast approaches 1.0; a real moon shadow reads as faint
  because the eye is dark-adapted and the whole scene is dim, which a monitor showing a brightened
  night cannot reproduce. So the ratio sets the curve's *shape* and this constant its *amplitude*.
  Before §6b it was quietly doing a second job — standing in for the washout the model could not
  express — and it no longer is.
- **A perceptibility gate, because a ratio is never exactly zero.** In daylight the strength is
  0.0002, not 0, so `MoonPosition.ShadowForMap` would otherwise report a shadow at every moment of
  every day and have §6a darkening `colors.shadow` for something invisible. Strengths that would
  render below `WeatherDimmingMath.PerceptibleDarkening` (§13a's threshold, reused rather than
  duplicated — it is a fact about vision, not about weather) truncate to 0.
- **Cloud stays §13's.** `AmbientSkyLux` is a clear-sky curve on purpose. A cloud deck both darkens
  the ground and destroys the direct beam, and folding both in here would let them partly cancel.
  `WeatherDimmingMath.ShadowContrastFactor` already owns the beam half and `Patch_ShadowStrength`
  applies it to whatever this returns.

**What this cost elsewhere.** Three committed scenarios moved, all recomputed rather than
re-toleranced: `moon_sun_clock.json`'s two handover pins (`0.801` and `0.881` → `1.0`),
`sun_clock_realistic_moon.json`'s hour-20 probe (`0.80` → `1.0`), and
`weather_shadow_visibility.json`'s gibbous night (`0.8531` → `0.8023`, the linear-phase term going
away). The §14 handoff-step tests in `MoonSunClockTests` and `SunClockModeMoonTests` now assert the
step is exactly **zero** instead of bounding it at the 0.155 single-clock baseline — §14's clock claim
still rides on their `moon_elevation` assertions, and the strength ones became §6b guards.
`sun_clock_realistic_moon.json`'s probe is a real loss of signal, noted in the file: at that tick
realistic mode is genuinely night and *still* casts no shadow, because a 4°-high moon cannot compete
with a 1 lux sky. `moon_shadow_twilight_fade.json` is the new scenario that pins the fade itself.

**Conflict risk.** None new. `IlluminanceMath` is pure and referenced only by `MoonMath`; no new
Harmony patch, no new vanilla member touched, and the three patches that consume moon shadows keep
their existing branch structure — what changed is the value the shared `MoonPosition.ShadowForMap`
adapter hands them.

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
(`NightRadianceMath.OverlayBrightnessFactor(CurSkyGlow, minBrightness)` — linear between two anchors:
fully black at/below `OverlayDarkGlow` (**0** — only a genuinely zero sky blacks the screen out) and
untouched at/above `OverlayFullBrightGlow` (= starlight + airglow + a full moon at zenith, spelled out
from §7's source constants).

That dark anchor sat at the starlight+airglow sum (0.04) for a while, on the reasoning that the
constant floors are "the baseline of darkness" and only moonlight should buy the screen back. That
conflated two different things and produced a visible bug: with the floors **on** and no moon, glow is
exactly 0.04 — the anchor itself — so the overlay went fully black, indistinguishable from having the
floors off. The **Atmospheric night glow** toggle therefore did nothing at all outdoors on a moonless
night, which is exactly where a player would look for it. Anchoring at 0 restores the intent: the
floors are meant to be *seen* outdoors (a moonless night keeps `0.04/0.19 ≈ 21%` of overlay
brightness — a faint starlit dark rather than a void), and true pitch-black is reached the way the
design always documented, by turning the floors off so glow lands at 0. The toggle is now what
distinguishes those two states on screen, which is its entire job. Indoors is unaffected either way:
§7b occludes the sky for roofed cells, so the floors never showed there.

Injecting at the composed overlay, not in a `SkyTarget` postfix, is deliberate: it darkens
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

**`minBrightness` is no longer read straight from settings.** §19 may raise it while the sun sits in
the ozone twilight band, because polar twilight is dim but emphatically not black and the blue would
otherwise be multiplied into darkness right here. This is **the mod's only sanctioned visual-only
brightness floor**, and it is visual-only by construction rather than by care:
`OverlayBrightnessFactor` feeds nothing but the two material colours above, so `GlowGrid`, plant
growth, solar output and Dub's Skylights never see it. Note this patch's existing gate is already
correct for that purpose — if either feature is off nothing darkens the overlay, so there is nothing
to floor. Do not "fix" it by hoisting the floor out of the gate. See §19.

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
baked and raises that alpha: full cover (255) for *interior* cells, so an unlit interior is lit by its
lamps or not at all. Per-cell logic is the pure `IndoorOcclusionMath`; the patch is a thin adapter that
reads `map.roofGrid` / `map.edificeGrid` and rewrites `mesh.colors32`.

- **We reuse vanilla's classification and change only its magnitude.** Vanilla already decides, per
  vertex, which cells count as covered, and we ask the same questions in the same order — its corner
  pass ORs `roofDef != null && (roofDef.isThickRoof || thing == null || !thing.def.holdsRoof || thing.def
  .altitudeLayer == DoorMoveable)` over the four cells at the lattice point; its centre pass forces 100
  only when `Roofed(c) && (thing == null || !thing.def.holdsRoof)` and otherwise averages that cell's own
  four corners. Writing 255 where vanilla writes 100 therefore keeps the *shape* of the shading identical
  to what players already see from vanilla roof cover, and makes only its depth ours. Two consequences,
  both of which the first cut got wrong and both of which were visible on screen:
  - **A wall is a boundary, not an interior.** A wall cell is roofed (it carries the roof it holds up),
    so classifying on `Roofed()` alone painted every exterior wall dead black and pushed the darkness one
    cell past it onto open ground. `holdsRoof` excludes it, exactly as vanilla does — *unless* the roof is
    thick, since a mountain buries whatever is under it (vanilla's `isThickRoof` disjunct).
  - **Corners OR, boundary centres average.** A corner is covered if *any* of its four cells is interior,
    so every vertex inside a room lands on 1.0 and the interior renders flat. A cell that is not itself
    interior takes the mean of its own four corners, so an exterior wall is `1.0` on its inner face,
    `0.5` at its centre and exactly `0.0` on its outer face: the whole fade is spent on the wall tile and
    nothing beyond the building is touched. Averaging the corners *instead* — while still forcing every
    roofed centre to a hard 1.0 — is what produced the reported artefact: the mesh fans four triangles
    out of each centre vertex, so a centre that disagrees with its corners shades as a diamond, and the
    boundary read as a row of black radial blooms rather than a straight edge. Cells outside the map
    contribute nothing to the OR, which needs no special case and keeps the two sections that each bake
    a shared boundary vertex in exact agreement (no 17-cell seams).
- **"Roofed" means enclosed, not merely covered.** The one place we deliberately classify *narrower*
  than vanilla. Asking `roofGrid.Roofed(cell)` outright blacked out porches and overhangs at
  noon — they are roofed but stand open to the sky on their exposed sides. The `roofed` input
  is therefore `EaveCells.Encloses`, which also requires the cell's room to hold its own temperature
  (§15). Ungated, and shared with §15's shadow half so the two can never disagree about which cells
  are inside.
- **Leaky doors.** Vanilla lumps doors in with roof for cover (`altitudeLayer == AltitudeLayer.DoorMoveable`
  is one of the disjuncts that sets its corner flag) and a closed door's `blockLight` suppresses glow too,
  so at full occlusion a doorway would go dead black. A door is instead treated as a boundary cell like a
  wall — never interior, so it can never propagate darkness outward through the wall line — and
  `DoorSkyLeak` (default 0.15) applies as a *cap* on the corners it touches, leaving the threshold a shade
  brighter than the wall either side of it (centre `(1 - leak) / 2` against the wall's `0.5`). The door
  test mirrors vanilla's own so the two can never disagree about which cell is a doorway.
- **Only ever raises the baked alpha.** Other mods legitimately write it: Dub's Skylights nulls
  `map.roofGrid` across `Regenerate` so skylit cells never take vanilla's roofed branch, and Biomes!
  Caverns transpiles the roofed test so cavern roofs read as open. Taking `max` means we can add occlusion
  without undoing anyone's decision to let light *in* — worst case we leave their value alone. The patch
  also takes `Priority.First` so it runs before Dub's Skylights' Postfix restores the roofs it removed,
  and therefore sees skylit cells as unroofed. (Biomes! Caverns' intent is the opposite of ours by design;
  with both installed, our toggle is the one to turn off.)
- **One floor reaches interiors, and only through here.** Nothing that lifts `CurSkyGlow` can brighten
  a sealed cave by one shade — roofed cells take no sky glow at all. So **minimum indoor brightness** is
  applied as a *cap* on occlusion (`1 - floor`), leaving exactly that fraction of sky bleeding in. It is
  part of the preset bundle, so Realistic leaves interiors free to go fully black (the point of the
  feature) and Cinematic holds them at 0.50; at 1 it cancels occlusion outright, which is exactly
  equivalent to switching the feature off, a property of the formula rather than a special case. The cap is applied to corners
  *before* they are averaged into a boundary cell's centre, so a floored interior still ramps down across
  its walls rather than the wall flattening out at the floor value.
- **Baked, not per-frame.** Unlike §7a's material colour, these alphas only change when a section is
  dirtied, so `IndoorOcclusionRedraw` forces a `WholeMapChanged(GroundGlow)` when the toggle or either
  slider changes (it compares the *resolved* floor, so either knob moving is caught without duplicating
  the max() rule) — otherwise the setting appears to do nothing until something else dirties the map.
- **One resolution per cell, not one per read (`SkyOcclusionWindow`).** The lattice reads each cell up
  to five times — four times as a neighbour of a corner vertex it meets at, once as its own centre — and
  each read reaches `EaveCells.Encloses`, i.e. potentially a `Room` query. At `Section.Size == 17` that
  was `18*18*4 + 17*17 == 1,585` live-map resolutions per regenerate, and the trigger is not the frame
  but `MapMeshFlagDefOf.GroundGlow`, which *any* glower change dirties (a lamp toggling, a fire growing),
  so the cost lands in bursts on ordinary gameplay events. The postfix therefore bakes a
  `(Section.Size + 2)^2 == 361`-cell verdict window up front and reads it — the same trade §15's
  `EaveShadowGrid` already makes for the same reason, deliberately solved the same way rather than a
  second way: same one-cell skirt, same clip at the map edge, same allocate-per-regenerate (361 bytes,
  and a shared static buffer would corrupt the mesh if anything ever re-entered `Regenerate`). Pure
  refactor — every vertex alpha is identical, which `SkyOcclusionWindowTests` pins by running both
  lattices and comparing. Two properties it preserves rather than papers over: `EaveCells`' `roof == null`
  short-circuit still keeps the room query off unroofed cells (the window resolves exactly the cells the
  lattice was already resolving, no more), and the corner pass's old `cell.InBounds(map)` guard now lives
  in the window, which answers for an off-map cell with the same `false` that guard was contributing.
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

## 14. Sun-clock reconciliation (`SunClockMath` / `SunClock` / `Patch_SunGlow`)

**Problem.** Every visual subsystem keys on `SolarPosition.ElevationForMap` — a real
latitude/declination/hour-angle model. Vanilla's brightness keys on `GenCelestial`'s sun, which is not
a physical model: it lifts the sun bodily toward the surface normal (19° scaled by
`SunPeekAroundDegreesFactorCurve`, plus another 17° fading out from the equator to 60°) and damps the
seasonal term to a flat 0.2 below 70° latitude. The two disagree, and the disagreement shipped:

| hours/day with vanilla's sky lit but our sun below the horizon | winter | equinox | summer |
|---|---|---|---|
| lat 0° | 4.80 | 4.70 | 4.80 |
| lat 30° | 5.17 | 4.17 | 3.40 |
| lat 45° | 6.13 | 4.37 | 3.10 |
| lat 60° | 8.63 | 5.20 | 5.15 |
| lat 70° | 15.32 | 9.30 | 0.00 |

That is bright ground casting no shadows, for hours a day, at every ordinary latitude.

**Two modes, because the fix trades against itself.**

*Locked to vanilla (default).* Warp our sun's clock so it crosses the horizon exactly when vanilla's
sky does. Both suns are symmetric about solar noon — vanilla rotates by `(dayPercent - 0.5) * 360` and
takes a dot product; our `HourAngleDegrees` uses the identical convention — so a whole day collapses to
one number, the half-day fraction `h`. The warp maps daytime onto daytime and night onto night,
linearly, anchored at noon and midnight. Sunrise lands on sunrise **by construction**: the day-length
error is not small, it is zero, at every latitude and season. No glow patch, so no gameplay change.

`SunClock` measures vanilla's `h` by bisecting `GenCelestial.CelestialSunGlow` — the live function,
~20 samples cached once per in-game day per tile. Re-implementing vanilla's curve was rejected: it is a
pile of tuned constants that would silently rot the first time Ludeon retunes any of them, producing a
"match" that no longer matches.

*Realistic (opt-in).* Postfix `GenCelestial.CelestialSunGlowPercent` — private, but the single funnel
every glow path runs through, and it takes primitives — so vanilla's glow follows our sun instead.
Correct poles, real arctic summers, working southern hemisphere. Costs ~1.5 h average day-length change
within ±50° (worst 2.75 h), which moves growing hours and solar output. Hence opt-in.

**Why the glow map needs three anchors.** Vanilla's own mapping is `glow = sin(elevation)/0.7`, putting
its `IsDaytime` bar (glow > 0.6) at a sun 25° up — only reachable because vanilla lifts its sun. Feed a
physical sun through it and daytime collapses 5+ h at the equator, and polar summer registers as
permanent *night* since a polar sun never clears 25°. Fitting a single scale factor to day length
instead gives K = 6.3°, which matches day length but saturates glow by 6° of elevation, collapsing dusk
from vanilla's ~240 min to 37 min and squeezing §2/§8/§11/§12 (all glow-keyed) into a sliver. Splitting
the anchors fixes both at once, because day length depends only on the 0.6 crossing and the dusk ramp
only on the 1.0 crossing — they are independent knobs:

| | dusk (glow 1.0→0.1), lat 45 summer |
|---|---|
| vanilla | 239.9 min |
| single-K fit (6.3°) | 37.2 min |
| three-anchor (0 @ −0.83°, 0.6 @ 3.8°, 1.0 @ 45°) | 267.3 min |

**Vanilla quirks locked mode inherits** (measured, and the reason realistic mode exists at all): a 5°
polar cliff — 70° is still flat at 13.9 h summer, 75° is fully binary; the poles get *zero* daytime at
the equinoxes, because vanilla's seasonal term is exactly 0 there and glow peaks at
`cos(75°)/0.7 = 0.37`; and the southern hemisphere never gets a true polar day, because both latitude
curves are evaluated on **signed** latitude and clamp below 70, so −89° is treated as tropical. Note
`SunPosition` uses `Mathf.Abs(latitude)` for its third rotation and raw signed latitude for the two
curve lookups, three lines apart — which is what makes oversight more likely than intent.

**The moon rides the same clock (§6).** §14 originally warped only the sun, on the reasoning that the
moon "has its own rise and set". It does not: `MoonMath.MoonDayPercent` exists to produce
`moonHourAngle == sunHourAngle − elongation`, so the moon is defined *relative to* whichever clock the
sun is on. Leaving it on the raw day percent made it lag a sun that no longer existed, by exactly the
day-length gap the warp closes — 1.5–3.1 h within ±60°, 7.7 h at lat 70 in winter. Measured, with the
pre-§14 single-clock baseline in brackets:

| full moon, measured against vanilla's sky | raw-clock moon | fixed | [pre-§14 baseline] |
|---|---|---|---|
| moonrise off sunset | 2.2–3.3 h (4.97 worst) | 0.15–0.32 h (0.90 at lat 60 winter) | 0.11–0.37 h |
| hours up in a lit sky | 3.5–6.5 (13.2 at lat 70) | 0.31–0.64 (1.81 at lat 60) | 0.22–1.36 |
| new moon's elevation off the sun's | 12–35° | 0° | 0° |
| shadow strength at the dusk handoff | 0.280 (full) | 0.155 | 0.155 |

That last row is the visible one. Both shadow patches switch from the sun's shadow to the moon's at the
sun's horizon crossing, where `ShadowIntensityFromElevation` has ramped the sun shadow to 0 — and the
moon shadow then starts wherever the moon's elevation puts it on a ramp only 3° wide. A raw-clock moon
was already 10–36° up there, so the shadow snapped straight to full `MoonShadowMaxStrength` pointing
somewhere unrelated, on every clear night around full moon.

Fix: `MoonPosition.SkyForMap` takes its day percent from `SolarPosition.Inputs` instead of calling
`GenLocalDate.DayPercent` again. Note it restores the baseline rather than reaching zero — the
refraction horizon enters both sunrise equations with the same sign while a full moon's declination is
the sun's *reflected*, so the windows are not exact complements and a full moon sits ~0.8° up at sunset
regardless of clock. The residual grows with the half-day gap (0.90 h at lat 60 in winter), because the
warp stretches the moon's arc by the *sun's* ratio, which is not quite the moon's. Pinned by
`MoonSunClockTests` offline and the `moon_sun_clock` live scenario, which A/Bs the two clocks through
the dev-only `moon_clock_warp` bridge.

**Conflict risk.** Realistic mode is the mod's only patch that changes a gameplay-facing value, and it
is off by default. The modes are an enum, not two bools, because each defines itself in terms of the
other; `Patch_SunGlow` additionally carries a reentrancy guard, since `SunClock` measures vanilla by
calling the very function that patch postfixes.

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

On an Odyssey space map the curve pins flat to `ZenithKelvin` and `TintStrength` goes to `0` via the
shared `inVacuum` gate — the whole ramp is a Rayleigh reddening model and there is no air path to
redden through. See §18 for why both halves are needed.

This subsystem's `NightFadeFloorDegrees` (−6°) is also where **§19 takes over**: below the horizon
the warm tint hands off to ozone (Chappuis) twilight blue, which ramps in from −4°. The two overlap
deliberately across that 2° window, because real dusk has a warm band low in the west under an
already-blue vault. §19 is emphatically *not* an extension of this curve — it models an absorption
notch rather than Rayleigh reddening, and it inverts both of this file's tested invariants
(monotonicity, and R ≥ G ≥ B). See §19.

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
  `0` at/above `OnsetGlow`, `1` at/below `FullGlow` (0.05, a small nonzero floor so the shift
  completes while it's still bright enough to see).

  `OnsetGlow` is **0.50**, anchored on RimWorld's own definition of "fully lit" rather than on
  taste. `Verse.GlowGrid.GroundGlowAt` caps ordinary artificial light at exactly 0.5
  (`b = Mathf.Min(0.5f, b)`) and `PlantProperties.growMinGlow` is 0.51 — which is precisely why sun
  lamps need `GroundGlowAt`'s `accumulatedGlowAt.a == 1 → return 1f` escape hatch to grow anything.
  So 0.5 is the brightest an ordinary lamp-lit cell ever reads, and a lamp-lit room must render at
  full colour; anything dimmer is, by the game's own measure, less than fully lit.

  The ramp between the anchors is **not linear**. Moving the onset up to 0.5 stretches it across the
  whole dusk band, and a linear ramp would drain ~20% of the scene's colour at glow 0.35 — exactly
  §2's twilight peak, where §2 and §8 are warming the sky and golden hour is supposed to read warm
  rather than grey. `RampExponent` (2.75) eases the curve in so it hugs zero through the top of its
  range and only bites once the scene is genuinely dim. The exponent is derived from that
  constraint, not chosen by eye: at glow 0.35 the normalised position is `(0.50 - 0.35) / 0.45 =
  1/3`, and `(1/3)^2.75 ≈ 0.05`, so the twilight peak keeps ~95% of its saturation.

- Applies the factor as a **cool tint** on `colors.sky` / `colors.overlay` (peak blends 0.50/0.35,
  scaled by `PurkinjeSettings.TintStrength` — the "Night desaturation" slider). Lerping rather than
  overwriting preserves each `WeatherDef`'s palette. `__result.glow` is never touched.

  This is a *secondary* cue and cannot be more than one. The desaturation itself is a separate draw
  layer, below.

**§19 stacks with this deliberately**, with no cross-subsystem suppression. They model different
things — §9 is the *eye* losing colour discrimination as rods take over, §19 is the *sky* genuinely
being blue from ozone absorption — and a real polar twilight is both at once, a saturated blue over
a scene whose own colours have drained. On a high-latitude winter day both fire together; the
ordering error between their two lerps is bounded at ~0.063 (≈16/255), which is a subtle hue
difference rather than an on/off one, so neither carries a `HarmonyPriority`. See §19.

### Why the desaturation needs its own draw layer

Desaturating is `lerp(colour, grey, t)` — it needs the pixel's own colour. Two channels were tried
and both are measurably incapable of it; the history matters because each looked correct in code
review and failed only against a running game.

**1. `SkyColorSet.saturation` — global by construction.** That field is assigned to
`Find.CameraColor`, a `ColorCorrectionCurves` **image effect** over the finished frame. It cannot
tell a campfire from the dark ground around it, so flames came out as grey as the dirt — backwards,
since scotopic vision keeps colour in bright sources and loses it in dim surroundings. Measured on a
live A/B: it dropped the flame core from 0.836 saturation to 0.401. No tuning fixes a channel that
has no access to *where* a pixel is.

**2. `SkyColorSet.sky` — a multiply, not a blend.** The replacement dropped the global multiply and
lerped the sky tint toward a fixed cool blue-grey `CoolNight = (0.55, 0.60, 0.72)`. It did exempt the
fires (flame core 0.836 → 0.836), but it desaturated nothing, for two independent reasons:

- vanilla's Clear night sky is already `(0.482, 0.603, 0.682)`, so the "target" was within a few
  percent of the current value — and *warmer* than it. Unlit ground moved 0.152 → 0.153 saturation;
- more fundamentally, `colors.sky` becomes `MatBases.LightOverlay.color`, which the overlay
  **multiplies** the scene by. Confirmed by building the honest version — drain that colour toward
  its own luminance grey — and running it: unlit dirt went *up*, 0.398 → **0.488**, because
  neutralising the blue light let the ground's own brown show through. A multiply scales channels; it
  can never pull them toward each other.

**3. What actually works: alpha compositing.** Blending toward a grey with alpha `t` *is*
`lerp(colour, grey, t)`, and alpha can vary per vertex. So the desaturation is a map draw layer of
our own — `SectionLayer_NightDesaturation` — and needs no replacement shader for
`MatBases.LightOverlay`, which is what made the per-cell version look impossible. It is a new mesh
drawn alongside vanilla's, not a hijack of one, so it collides with nothing (Dub's Skylights, the
Perspective family).

  - **Registration is free.** `Verse.Section`'s constructor instantiates every non-abstract
    `SectionLayer` subclass it finds, so declaring the class is the registration — no Harmony patch.
  - **Per-cell alpha** comes from `GlowGrid.GroundGlowAt(cell, ignoreSky: true)`, mapped by
    `NightDesaturationMath.CellWash`: full wash on an unlit cell, falling linearly to **exactly zero
    at 0.5 glow** — RimWorld's own artificial-light cap (`GroundGlowAt` clamps ordinary lights to 0.5;
    `growMinGlow` is 0.51). A campfire's own cell sits at or above it, so it renders at standard
    colour by construction rather than by tuning. `ignoreSky` matters: the sky's contribution is
    already the whole of `PurkinjeFactor`, and counting it per cell would make a brightening sky
    exempt the outdoor cells the effect is for.
  - **Map-wide strength** is the material's alpha, rewritten each frame by
    `Patch_NightDesaturationStrength` from the same factor the tint uses. Split this way for the
    reason vanilla splits its own overlay: the per-cell part only changes when the glow grid does
    (mesh, rebuilt on `MapMeshFlagDefOf.GroundGlow`), the sky part changes continuously through dusk
    (one material write). Baking the second into the mesh would rebuild every section every few
    minutes of game time.
  - **Altitude `Weather`**, i.e. *below* `LightingOverlay`. The wash lands on the scene first and
    vanilla's night multiply darkens the result afterwards, instead of sitting on top of the darkness
    as grey haze. It is above every thing/pawn altitude, so an item on unlit ground desaturates with
    the ground it lies on.
  - **`WashGrey` is dark (0.11)** because alpha compositing lifts whatever it blends toward; a
    mid-grey would raise black night ground into a haze and undo §7a's pitch-black nights.
  - Corner and edge vertices are averaged over the cells they touch (4 / 2 / 1), the same smoothing
    vanilla's lighting overlay does, so the wash gradients around a light instead of drawing squares.

  #### What the layer costs, and the two things that stopped it costing that (issue #20)

  Live measurement of every section layer on the map (the fan-out table, §16) landed on this one:
  **271 µs per section regenerate, the most expensive section layer in the game on a modded map,
  vanilla's included, and 67% of everything this mod adds to a `Roofs` dirty flag.** That is not a
  rare path — `GlowGrid.DirtyCell` raises `Roofs` *and* `GroundGlow`, and this layer subscribes to
  both, so every lamp toggle, every fire growing or dying, and every sun lamp cycling at dawn paid it.

  - **The averaging above was reading the glow grid nine times per cell.** Each of the 289 cells in a
    section asks about itself and its eight neighbours, so one regenerate cost 2,601
    `GlowGrid.GroundGlowAt` calls over a 19x19 = 361-cell footprint — every interior cell queried nine
    times for the same answer. `Source/NightWashWindow.cs` bakes that footprint once and the vertex
    loop reads it: **2,601 → 361 glow queries, a 7.2x reduction**, with the mesh byte-identical.
    Vanilla's own `GenerateLightingOverlay` walks a vertex lattice for exactly this reason, and this
    is the third time the same problem has been solved the same way here — `EaveShadowGrid` (§15) and
    `SkyOcclusionWindow` (§7b) are the other two, and the three deliberately share one shape:
    `readonly struct`, static factory, section-plus-one-cell-skirt clipped at the map edge, allocated
    per regenerate rather than pooled (a shared buffer would corrupt the mesh under re-entry, and
    ~1.4 KB on a call that never fires per frame is not worth the risk).
  - **The layer paid all of that with the feature switched off.** `Verse.Section.TryUpdate` does not
    consult `Visible` — only `DrawLayer` does — so `Regenerate` ran in full for every player who had
    §9 unticked, building a mesh nothing would ever draw. It now returns early. Two consequences had
    to be handled rather than assumed:
    - `TryUpdate` clears a layer's `Dirty` flag even when `Regenerate` returns early, so the early
      return marks the sub-mesh `disabled` on the way out. Otherwise a mesh baked before the toggle
      sits in `subMeshes` describing a glow grid that has since moved, ready to be drawn the instant
      the feature comes back. Disabling rather than clearing the colours is deliberate: `Clear` +
      `FinalizeMesh` would re-upload the mesh, which is a large part of what the gate exists to avoid.
    - Nothing is left marked dirty by the time a player ticks the box back on, so
      `NightDesaturationRedraw` dirties `GroundGlow` map-wide on the change — the same
      change-detected shape as `IndoorOcclusionRedraw` (§7b) and `EaveShadowRedraw` (§15), added for
      the same reason: without it the setting reads as having done nothing until the next lamp or roof
      edit happens to rebuild the sections.

  **Measured, live, both halves back to back in one sitting** (`Tests/Scenarios/layer_regen_timing.json`,
  the same probe and scenario §16's table came from; µs per section regenerate, mean of 10 timed runs
  over 4 roofed sections after 2 warmups):

  | | before | after | |
  |---|---|---|---|
  | `SectionLayer_NightDesaturation`, feature **on** | 298.9 | **104.8 / 107.3** | 2.8x faster |
  | `SectionLayer_NightDesaturation`, feature **off** | 280.1 | **0.013 / 0.015** | the gate |

  The feature-off row is the one worth reading twice: before the gate, a player who had §9 unticked
  paid 280 µs per section per lamp toggle for a mesh that was never drawn. It now costs nothing
  measurable — the same 0.01-0.02 µs floor `SectionLayer_Darkness` reports when it returns immediately.

  The 2.8x on the feature-on row is less than the 7.2x reduction in glow queries, and that is expected
  rather than disappointing: the queries were never the whole cost. What remains is the vertex
  arithmetic and the Unity mesh upload in `FinalizeMesh`, neither of which this change touches, and
  which now dominate what the layer does.

  Every other layer in the same runs was flat — lighting overlay 161.8 → 163.8, indoor mask 29.5 →
  30.7, gravship hull 60.6 → 59.0, sun shadows 25.6 → 26.0, eave shade 15.0 → 13.3, darkness 0.025 →
  0.025 — which is what makes the desaturation delta attributable to the change rather than to machine
  state drifting between the two runs.

  Recomputing §16's headline on these numbers: **what this mod adds to one `Roofs` flag per on-screen
  section falls from ~434 µs to ~241 µs, a 45% cut** (~136 µs for a player with §9 switched off). The
  ranking that motivated issue #20 also inverts — desaturation drops from 69% of what we add to 44%,
  and **§7b's postfix on vanilla's lighting overlay (~97 µs, still 2.5x the 67 µs layer it postfixes)
  is now the largest single term this mod contributes.** That belongs to issue #9, which now has both
  a number and first place.

  Offline coverage for both is in `NightWashWindowTests` (the pre-refactor per-read resolution against
  the windowed one, byte-identical vertex alphas across every section of a scene with lamps,
  campfires, lamp-capped light and map edges, plus the query counts) and `NightDesaturationGateTests`
  (IL inspection of the shipped assembly: the gate is in front of the work, the discard writes
  `disabled`, and the settings path still triggers the rebuild — none of which a unit test can reach,
  since `Regenerate` needs a live `Section`, `LayerSubMesh` and `GlowGrid`). `NightDesaturationMath`
  gained `WashAlpha`, moved out of the layer so the equivalence test compares shipped bytes; it keeps
  `Mathf.RoundToInt`'s banker's rounding rather than adopting `CoverAlpha`'s away-from-zero form,
  which is pinned by a test at the one value where the two disagree.

  Live A/B at hour 2, feature off → on: flame core 0.836 → 0.836, lamp-cap-lit ground 0.629 → 0.630,
  unlit ground 0.114 → 0.054. That is the split the subsystem always claimed and never had.

  `PurkinjeMath.SaturationMultiplier` is retained as the documented reference curve but is not
  applied — the wash's peak (`MaxWash`) is what replaced it.

  The falloff lives in `Source/PurkinjeMath.cs` and the per-cell wash in
  `Source/NightDesaturationMath.cs` (both System-only pure files, kept out of `Formulas.cs` to avoid
  colliding with the other in-flight subsystems editing it), with offline `[TestCase]` coverage of
  both plateaus, monotonicity, the eased midpoint, the artificial-light cap, the golden-hour
  constraint, the multiplier endpoints, and — for the wash — the lit exemption, the linear ramp, and
  the clamping of both inputs.

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

- **Unnatural eclipse only — the shipped default.** The original visual-only behaviour: no extra
  events, just the §10b reshape of the storyteller's eclipse. This is the default *because* it is the
  only one of the three that fires nothing and suppresses nothing, and the mod's contract is that no
  default setting alters gameplay. An earlier version shipped Both, on the reasoning that a natural
  eclipse every few game years is rare enough not to count; that was the wrong test. Rare is not
  never, and a real `Eclipse` `GameCondition` costs solar power and mood no matter how honest its
  timing is — so the one knob capable of breaking the contract now defaults to the side that can't.
- **Natural + unnatural (opt-in).** Geometric eclipses fire at real astronomical times (natural ramp)
  *and* the storyteller's random eclipses still occur (unnatural ramp); each active eclipse is
  darkened by whichever kind it is (`EclipseIntegration.RendersNatural` tags the ones we fired).
- **Natural only (opt-in).** Only geometric eclipses; the random storyteller eclipse is suppressed
  (`Patch_SuppressRandomEclipse`) so they don't double-fire. Note this cuts *both* ways against
  vanilla's rate: eclipses become rarer and shorter, not just differently timed.

Both opt-in modes are labelled `(changes gameplay)` in the settings radio, matching §14's sun-clock
wording, so the two gameplay-touching choices in the whole mod read identically.

The "Eclipse effects" checkbox is the master above the radio — off means the mod leaves eclipses
entirely alone (vanilla flat dim, vanilla timing, no trigger, no suppression).

### 10a. Natural eclipse

Driven by the modeled moon's real position (§6): when the moon geometrically transits the sun, fire
an eclipse that lasts the **correct, short real-eclipse duration** — the event triggers *during an
actual eclipse* and ends when the discs part. Astronomically accurate in both when it happens and
how long it lasts.

Because it changes *when* (and, by shortening the duration, *how long*) a gameplay event occurs
(solar-power loss, mood), it is the flavour gated by the eclipse-mode radio above — and, for exactly
that reason, the flavour that is **off by default**: it is active only in the two opt-in modes
(Natural-only and Both). Design consequences:

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

Same principle as §10: shift the night sky toward auroral colours while an auroral event is running.
**Visual only — the flare's electronics disruption, the aurora event's joy bonus, and every other
gameplay effect are left entirely untouched, and this blends only `SkyTarget.colors`, never
`SkyTarget.glow`, so it stays in the same low-risk colour-only lane as §2/§8 (see "Conflict risk"):
the brightness value other mods read is undisturbed.** Auroral emission colours (atomic-oxygen green
~557.7 nm, red ~630 nm) are physical constants, not mod-specific.

**Which events drive it — and nothing else does.** Exactly two conditions, resolved by
`AuroraConditions`: `SolarFlare` and vanilla's own `Aurora`. The gate is deliberately a def lookup
rather than anything keyed on darkness, because an aurora that turns up on an ordinary clear night is
worse than no aurora at all. Always-dark maps are excluded too, mirroring `GameCondition_Aurora`'s own
`IsAlwaysDarkOutside` guard. When both conditions are somehow active the `Aurora` event wins — it is
the named, player-facing event, so its colour cycle should be the one on screen rather than two
unrelated hues mixing. (`SolarFlare` is a core `GameConditionDef` but, unlike `Eclipse`/`Aurora`, is
not exposed on `GameConditionDefOf`, so it's resolved by defName via `DefDatabase.GetNamedSilentFail`.)

**Why tinting during a vanilla aurora doesn't double up — a corrected premise.** An earlier revision
of this section drove the tint from `SolarFlare` only, on the grounds that `GameCondition_Aurora`
*already* renders its own shifting sky colours via `GameCondition.SkyTarget(Map)`, so tinting again
would fight it. That reasoning was wrong, in the same way §13's `maxGlow` premise was wrong, and the
correction is the interesting part of this subsystem.

`SkyManager.CurrentSkyTarget` composes each condition with `SkyTarget.LerpDarken`, and
`SkyColorSet.LerpDarken` is `Color.Lerp(A.sky, A.sky.Min(B.sky), t)` — a **per-channel minimum**. A
game condition can therefore only ever *darken* the sky. Vanilla's aurora returns
`Lerp(white, currentColor, 0.075) · max(0.73, sunGlow)`, which at night is brighter than the sky in
every channel:

| clear night, green palette entry | R | G | B |
|---|---|---|---|
| `skyColorsNightMid` (weather) | 0.482 | 0.603 | 0.682 |
| `GameCondition_Aurora.SkyTarget` | 0.675 | 0.730 | 0.675 |
| `min()` — what actually renders | 0.482 | 0.603 | **0.675** |

The only surviving change is blue, shaved by ~1%. Every entry in vanilla's eight-colour palette lands
the same way, and its `glow = max(sunGlow, 0.25)` floor is min'd away just as completely, so it never
brightens the map either — despite the letter promising "undulating colors… make the night brighter".
What vanilla's aurora *does* still deliver is the saturation drop (1.25 → 1.0) and the SkyGaze joy
bonus, neither of which we touch. So this fills a hole rather than overpainting a render.

**Why not suppress vanilla's `SkyTarget` and replace it outright.** While the `Aurora` condition is
active its colour set is a per-channel *ceiling* on whatever we inject: green clips once the tint
passes ~0.32, after which green pins while red and blue keep falling, skewing the hue rather than
deepening it. At the shipped `MaxSkyTintStrength` of 0.18 that ceiling is nowhere near binding, so
suppressing vanilla would buy exactly nothing — in exchange for a second Harmony patch on a vanilla
method and a conflict surface with every other mod that touches auroras. (It was worth ~1.5% of one
channel back when the tint was 0.35; it is worth 0 now.) `SkyColorSet.LerpDarken` and
`SkyTarget.LerpDarken` are both asserted by `ApiCompatibilityTests`, so if Ludeon ever swaps that min
for a plain `Lerp` — which would make vanilla's aurora render for real — the driver set gets
re-decided rather than silently double-painting.

**Why the tint is weak, and what would make auroras vivid.** 0.35/0.15 shipped first and rendered as
a green filter over the whole world rather than as an aurora — the "flat neon" failure the constants'
own comment was trying to avoid, arrived at by arguing *up* from vanilla's 0.075 on the grounds that
we ship no overlay texture. The error is treating a flat wash and a textured aurora as the same
effect at different strengths. They are not: a real aurora is legible because it has *structure* —
bands, several colours at once, drift — and a map-wide uniform hue has none of that, so extra
strength only makes it read more like a colour grade. 0.18/0.08 is a tint you notice without it
grading the scene.

That reasoning held right up to its own conclusion: genuine vividness needs structure and movement
rather than a bigger number, so §11a below builds it, and **the flat tint now has two peaks rather
than one.** With the curtain drawing, this layer's job shrinks to seating the ribbons in a sky of the
right colour, and it steps all the way back to vanilla's own `0.075/0.025`
(`AuroraMath.CurtainedSkyTintStrength`). With the curtain off it keeps `0.18/0.08`
(`MaxSkyTintStrength`). Two pairs rather than one because the feature-flag rule in
`CelestialLightingFeatures` requires that turning §11a off restores exactly what the mod rendered
before §11a existed — a single lowered constant would instead leave the sky weaker than either
version ever shipped, and quietly invalidate the harness A/B that the flag exists to enable.

**Approach.** A Harmony Postfix on `WeatherWorker.CurSkyTarget` — the same injection point as
`Patch_TwilightColor` (§2). The two blend different, non-overlapping things (twilight warms the sky
at dusk-glow ~0.35; this tints only at deep night and only during an auroral event), so they stack
cleanly regardless of postfix order. The pure core (`Source/AuroraMath.cs`, offline-tested) supplies:

- a **night-visibility ramp** (`NightVisibility`) that fades the tint to zero as the sky brightens,
  reusing vanilla's own `GameCondition_Aurora.MaxSunGlow` (0.5) as the upper cutoff so the tint
  disappears at the same brightness vanilla's aurora does — auroras are invisible in daylight, so a
  daytime flare produces no sky colour;
- a **condition fade** (`ConditionRampFactor`) easing the tint in over the event's first ~hour and
  out over its last ~hour (combined with `Min`, so a very short event simply peaks lower than full
  rather than ever snapping in);
- a slow **green↔red shimmer** (`ShimmerRedMix` / `AuroralColorAtPhase`) advanced by game ticks,
  capped at `MaxRedMix` so the aurora stays green-dominant (as real ones mostly are), warming only
  partway toward the high-altitude red line at each cycle's peak.

**Hue depends on the driver** (`AuroraConditions.TintColorFor`). A solar flare has no colour of its
own, so it gets that green↔red emission-line shimmer. The vanilla `Aurora` event lends us its own
`CurrentColor` instead — already cycling through vanilla's eight-entry palette (greens, cyans, blues,
violets) on its own transition timer. Borrowing it gives the event the "undulating colors" its letter
advertises, keeps our render and vanilla's pointing the same direction in every channel (which is
what matters under the per-channel min above), and means an aurora event never just looks like a
recoloured solar flare.

The blend strengths (`MaxSkyTintStrength`, `MaxOverlayTintStrength`) are deliberately restrained —
see "Why the tint is weak" above for how they were set and why arguing them upward from vanilla's
~0.075 was the wrong move. `AuroraConditions.CurrentSkyTintStrength` is shared by the patch and the `aurora_tint` live probe so
they can never derive a different value from each other — the same discipline `SolarPosition.cs`
enforces between the shadow patches.

Both tint strengths take the shared `inVacuum` gate and return `0` on an Odyssey space map: the
630 nm emission this models sits ~630 km up and a platform sits at 200 km, so a full-screen tint is
the wrong presentation rather than merely too strong a one. See §18.

Deferred: a matching moonlight/HUD hook and per-condition settings sliders (the tint constants are
already isolated in `AuroraMath` for that). Lowest priority of the planned set.

### 11a. Aurora curtain — structure instead of saturation (`AuroraCurtainOverlay`)

**Problem.** §11 can only ever put *one* colour on the whole map at once, because it lerps
`SkyTarget.colors`, which is a single global value. So no tuning of it can produce what actually makes
a real aurora legible: several colours visible simultaneously, arranged in bands, drifting and
undulating, with brightness varying across the sky. Turning the one colour up doesn't approach that —
it just grades the frame greener, which is why #41 had to halve it. A flat wash and a textured aurora
are not the same effect at two strengths.

**Approach.** A `Verse.SkyOverlay` drawn over the map, textured with a procedurally generated,
tileable noise field that is regenerated a few rows per frame and UV-panned every frame.

**Where the ribbons come from.** The band-forming technique is ported from the *Aurora Borealis* Godot
shader by Klaufir ([godotshaders.com](https://godotshaders.com/shader/aurora-borealis/)), published
under **CC0** — public domain, so there is nothing to license. Worth being explicit that this is a
technique from an unrelated CC0 Godot shader and *not* from any RimWorld mod: the clean-room
constraint this repo is built under concerns Sjaandi's *Tilt Planet!*, whose code was never available
to anyone, and is untouched by this.

The insight is that you do not draw noise to get an aurora — **you draw the contour where two noise
fields are equal.** Subtract two fBm fields and push the difference through a `smoothstep`: the result
is ~0 where A wins, ~1 where B wins, and sweeps through 0.5 along the boundary curve between them.
Keying brightness on *proximity to 0.5* therefore lights a thin, closed, wandering curve — a ribbon —
instead of a field of blobs. A steep power chain (`AuroraCurtain.Amplify`, magic number `0.166504`
and all — it reads as `m⁴v² + mv⁴ + v⁸`) then crushes everything off the contour toward black, which
is what gives the curtain a defined edge rather than a soft gradient.

Two deliberate departures from the original:

- **The raymarching is dropped.** It exists to give the curtain volume in a 3D scene. RimWorld's
  camera is a fixed orthographic top-down, so there is no parallax to sell and ~100 samples per pixel
  would produce very nearly what one sample produces.
- **The two fields drift in *different* directions.** The original scrolls both together, so the
  pattern translates rigidly and all its apparent writhing comes from the static domain warp the
  fields slide beneath. Counter-drifting moves the boundary curve *non-rigidly* — ribbons stretch,
  fold and pinch. That is the "undulate" half of the requirement, and it is free: same sample count
  either way.

**Why a CPU texture and not a shader.** RimWorld 1.6 is Unity 2022.3.35f1 and mods *can* load custom
shaders from an `AssetBundle`, so this was a real option. Against it: a bundle must be built in the
matching Unity Editor and shipped as per-platform binary blobs, and this repo ships no binary assets
at all. In favour of the CPU: the cost is bounded by *resolution*, and an aurora is the one effect
that loses nothing to blur. So the field is baked small — 192² for the shipping field — and drawn
over the map as a small number of bounded quads (see *Sheet geometry* below).

**Pure core / adapter split**, as everywhere else:

- `Source/AuroraNoise.cs` — tileable value noise + fBm, with **separate X and Y periods**. Anisotropy
  is not a flourish: auroral arcs are long bands, and with one period the only available shapes are
  round. Not `Mathf.PerlinNoise`, which is not tileable, is undocumented as to algorithm, and lives in
  `UnityEngine` — which would drag the generator out of the offline tests. **Tileability is
  load-bearing**: the texture is panned every frame, so a field that does not wrap shows a hard seam
  sweeping across the colony once per cycle.
- **Two fields, one contract.** `Source/AuroraCurtainHemRays.cs` is what ships: it *authors* the
  curtain silhouette directly in 2D — a bright wandering hem, brightness falling off above it,
  vertical striations cut into the falloff, three curtains summed additively. Every noise sample is a
  function of `u` alone, so a whole texture column shares them and the per-pixel loop does arithmetic
  only. `Source/AuroraCurtain.cs` is the original contour field, kept compiled and tested but not
  drawn — see §11b. `Source/AuroraFieldSpec.cs` describes either as data (resolution, tint weight,
  drift wrap, sheets), so the adapter, the cost probe and `Tools/AuroraPreview` all read the same
  numbers rather than three drifting copies. `AuroraMath` owns the primitives both fields share
  (`Amplify`, the hue-band edges, the emission colours), so neither field owns the other's.

  The contour field's palette runs violet → green → red, reusing `AuroraMath.OxygenGreen` (557.7 nm) and
  `OxygenRed` (630 nm) and adding `NitrogenViolet` (N₂⁺ first negative band, ~427.8 nm). Because fBm
  clusters near 0.5, green holds the middle and the coloured fringes appear at the tails — green-
  dominant with red above and violet below, which is what an aurora looks like, rather than a rainbow.
  A separate very-low-frequency `Envelope` gates the ribbons in and out so the aurora occupies *part*
  of the sky; without it the field covers the tile uniformly and drifts back toward §11's failure.
- `Source/AuroraCurtainOverlay.cs` — owns the `Texture2D`, the two materials and the refresh schedule.
- `Source/Patch_AuroraCurtainDraw.cs` — the draw hook.

**Additive, above the lighting overlay.** `ShaderDatabase.MoteGlow` at `AltitudeLayer.VisEffects`. Both
halves matter. Additive because an aurora emits light rather than replacing the sky behind it — under
alpha blending, a bright ribbon over a near-black night must be nearly opaque before it reads at all,
which pushes right back toward the flat wash. And `VisEffects` rather than `Weather` because Weather
sits *directly below* `LightingOverlay`: a weather-altitude aurora is multiplied by the night sky
colour, and with §7a driving that overlay toward opaque black the ribbons would be multiplied out of
existence in precisely the conditions they exist for. `VisEffects` is above the lighting overlay and
still below `FogOfWar`, so the curtain glows through the dark while unexplored map stays fogged.
`ApiCompatibilityTests` asserts that *ordering*, not merely that the enum members exist.

**What occludes it, and why that disagrees with vanilla weather.** Neither roofs nor fog of war hide the
aurora. Vanilla weather behaves the opposite way — measured, not assumed: with a block of `RoofRockThick`
laid over the map (`Tests/Scenarios/roof_check.json`), rain's streak variation drops from 11.7 to 4.7
under the roof while the aurora carries across the boundary at full strength. That places
`SectionLayer_IndoorMask` between `AltitudeLayer.Weather` and `VisEffects`; it could not be read from the
code, because those materials are `MatLoader` assets whose render queue lives in a Unity bundle rather
than in `Assembly-CSharp.dll`.

The inconsistency is deliberate, and it is about how a player *thinks* rather than about physics. **An
aurora is ~100 km up; rain lands on your head.** A roof stopping rain matches intuition exactly, so
vanilla is right to occlude it. A roof stopping a light in the upper atmosphere does not — you would
still see it through the gap you are standing in, and a colonist under a mountain is not the audience
anyway. The same argument covers fog of war: an aurora is not hidden by the player's ignorance of the
terrain beneath it, so §11a draws one `AltInc` above `FogOfWar` and, in strict physical terms, both games
are being inconsistent in opposite directions. This one picks the reading a person would expect.

The ordering is pinned by `ApiCompatibilityTests`, not just the member names — a renamed `AltInc` stops
the mod compiling, whereas a reordered `AltitudeLayer` leaves it compiling and quietly drawing the aurora
under the fog or over `WorldClipper`.

**Cost, and where it actually goes.** The shipping field's noise is a function of `u` alone, so a whole
texture column shares it — 19 samples per column, none per pixel. The adapter bakes 6 rows a frame, and
originally rebuilt that table on every one of those calls, which turned the saving into *19 samples per
column, thirty-two times over*: ~3,600 samples a frame to fill 1,152 pixels. The table is now built once
per refresh sweep and reused across its slices.

Measured on Mono in the live harness, that took a frame of regeneration from **828 µs to 425 µs**. Note
the discrepancy: samples fell ~22×, cost fell ~2×. With the noise hoisted, **per-pixel arithmetic is the
bottleneck**, so resolution is now the expensive knob — the opposite of the contour field, and the reason
`aurora_curtain_cost` is pinned tightly rather than left as a "did it explode" guard.

One deliberate behavioural consequence: a sweep's `time` is pinned to the instant the sweep began rather
than advancing row by row. That is a fix rather than a compromise — the rolling refresh already relies on
rows baked frames apart differing imperceptibly, and pinning makes the tile self-consistent instead of
shearing slightly in time between its bottom and top, which it quietly did before.

**Sheet geometry — why this is no longer one map-wide plane.** The first build drew the field on
`MeshPool.wholeMapPlane` through `SkyOverlay`'s own helper. That plane is 2000 world units across with
its UVs pre-multiplied by 200 (`MeshMakerPlanes.NewWholeMapPlane`), i.e. one repeat per ten cells, and
the adapter divided that out to reach the feature size it wanted. **It tiles in both axes.**

For the contour field that is harmless — an overhead view of a wandering ribbon, where one patch looks
much like another. For the hem-rays field it is fatal, because **its v axis is not map-north; it is
altitude up the curtain**. Repeating v does not tile a texture, it stamps the same three arcs, hems and
all, up the map: ~1.6 copies of a distinctive stack on one screen at 76 cells per repeat, and 3.3 up a
250-cell map. It reads as wallpaper.

So the overlay owns its own quad and draws a *sheet* per display, each with its **v scale pinned to
exactly 1** — one vertical repeat, so vertical tiling is arithmetically impossible rather than tuned
away, at any map size and any zoom. Horizontal repeats remain and are the acceptable kind: a hem line
repeating every 150 cells is well past one screen, and each sheet carries its own u phase and may be
mirrored in u, so no two read as copies. `AuroraSheetLayout` places them at deliberately **irregular**
fractions of map height — evenly spaced sheets are periodicity wearing a different hat — computed in
floats from `Map.Size`, never from `Map.Center`, which is `Size.x / 2` in integer arithmetic and so half
a cell off on every even-sized map, and every stock map size is even.

The sky between and beyond the sheets is genuinely empty. That is intended: a real display occupies a
band of sky, not all of it, and the effect must not obscure the game underneath. §11's flat tint still
colours the whole sky faintly, so the gaps are not black. It also makes "one giant aurora" a layout
value rather than a code path — a giant is a single sheet with a large `CellsPerRepeatY`.

**Display lifecycle — an aurora is a sequence, not a patch** (`AuroraDisplays`). The first bounded
build placed *one* display when the aurora began and held it, unchanged, until the condition ended.
On a solar flare that is a single static patch of sky lit for a game day, and if it landed somewhere
the player never pans to, they never learn there was an aurora at all. A real display is a sequence:
arcs gather, hang for a while, dim, and are replaced by others elsewhere.

So there are four **slots**, and a slot is not a display — it is a channel that repeatedly spawns one.
Slot *s* cycles with period `CycleTicks[s]`, lit for `LifeTicks[s]` at the start of each cycle and dark
for the rest, offset by `PhaseTicks[s]`. Each pass is a **generation**, and the generation goes into
the seed, so a slot relighting is a genuinely new display — new size, position, mirroring, peak alpha —
rather than the same patch blinking. That is why the seed is `(event, slot, generation)`.

Three constants carry the argument, and each has a test rather than a preference behind it:

- **The periods are 100× four primes** (9700 / 11300 / 12700 / 16300). Round periods resynchronise
  every few in-game hours and the sky visibly pulses; pairwise-coprime ones cannot, and the pattern's
  true repeat is ~400 in-game years.
- **The duty cycles are all ~0.65 while the periods are not.** Each slot owns a fixed horizontal band
  of the map, so a slot lit more of the time than its neighbours would paint a permanent north–south
  brightness gradient across the colony for no physical reason. Equal duty, unequal period: the bands
  are equally busy and never busy together in a repeating way.
- **Lifetimes land at 2.5–4.2 in-game hours**, from those two. Long enough to be a display you watch,
  varied enough that two spawning together do not die together.

Bands are also how concurrent displays avoid landing on top of each other, and the guarantee is
arithmetic rather than probabilistic — two displays in different z bands cannot overlap whatever their
x or size, where the alternative (resample until nothing collides) is a loop whose cost depends on
luck inside a per-frame path. Fixed bands are periodicity, which is what the rest of the layout exists
to remove, and that objection would hold if a band were all a display had; within its band a display
still picks its own z, its own x across the whole map, its own size, mirroring and peak alpha, and is
replaced entirely every few hours. The band fixes only "not on top of the last one".

The sky is completely empty about **2% of the time, in stretches of at most ~1.2 in-game hours**, and
two or more displays share it ~87% of the time. The lull is deliberate: a display that never pauses is
a colour filter rather than an event, and §11's flat tint still colours the sky underneath one.
`AuroraDisplaysTests` pins both ends of that trade, plus the coprimality, the equal duty, and the
monotonicity of the per-display fade — properties of a whole night, which is exactly the class of thing
watching a live aurora for five minutes cannot check.

Per-display alpha is **seeded per display, not ranked per slot**. The pre-band layout gave slot 0 alpha
1.0 down to slot 3 at 0.35, which was right when slots were arbitrary; once a slot owns a band it makes
the southernmost strip permanently the brightest. Seeded, the bright one is somewhere different every
time.

`wholeMapPlane` is a **shared static mesh** used by every vanilla weather overlay, so adjusting its UVs
was never an option; it would have altered rain and snow for every mod in the load order. The quad's
vertex order, UVs and winding are copied verbatim from decompiled `MeshMakerPlanes.NewPlaneMesh` rather
than reasoned out, because a quad wound the wrong way renders as *nothing at all* — no error, no
warning, no clue.

**Why not `GameCondition.SkyOverlays`, which is the obvious hook.** #42 proposed it, and decompiling
1.6 shows it cannot work for a mod that does not own the condition:

1. `SkyManager.UpdateOverlays` **never draws**. All it does with that list is call `SetOverlayColor`.
   Drawing happens in `GameCondition.GameConditionDraw`, which is `virtual` and per-condition — and
   neither `GameCondition_Aurora` nor `GameCondition_DisableElectricity` overrides it, because neither
   has an overlay in vanilla. Only `GameCondition_UnnaturalDarkness` does.
2. **The colour is not ours to choose.** It passes `curSky.colors.overlay` with
   `alpha = SkyTargetLerpFactor`. `ForcedOverlayColor` overrides the RGB, but the alpha is still
   clobbered — discarding the night-visibility-and-fade ramp that makes this a night effect at all.
3. `SkyOverlays` is `virtual`, so patching the base would silently stop applying to any condition that
   overrides it.

So we own the whole lifecycle and postfix **`GameConditionManager.GameConditionManagerDraw(Map)`**
instead: the exact point in `Map.MapUpdate` where vanilla draws condition overlays, non-virtual, and
already inside the `drawingMap && Find.CurrentMap == this` branch. It recurses into `Parent`, so the
postfix guards `__instance == map.gameConditionManager` to fire once rather than twice. A
`MapComponent` would have needed no patch at all, but `Map.ExposeComponents` scribes the component
list into every save, making one close to irreversible — see the `MapComponent_SunShadowAxis`
tombstone for what removing one later costs.

**Performance.** This is the only part of the mod doing per-pixel CPU work, so it has a budget and a
measurement rather than a hope.

The dominant term is structural: everything is gated on
`Aurora && AuroraCurtain && ActiveTintDriver(map) != null && strength > 0`, evaluated *before* any
allocation. A solar flare or `Aurora` event is rare, short and night-only, so for almost all of a
playthrough §11a is one null check per frame and the texture is never allocated. It is a rare-event
effect, not an always-on one.

While an aurora *is* running, the field is refreshed 6 rows of 192 per **tick** — a full sweep every 32
ticks, about half a second. Rows baked a sweep apart differ invisibly, and `AuroraNoise`'s determinism
guarantees the overlapping lattice agrees exactly, so there is no seam at the slice boundary.

**Where the smooth motion comes from — and the claim that expired.** This paragraph used to say
regeneration could be coarse because "the GPU pans the texture every frame, supplying all the smooth
motion, while regeneration supplies only change of shape". That was true of the whole-map spanning
plane and **stopped being true when sheets became bounded patches**: a bounded patch cannot pan its
UVs — with a non-zero offset and `Repeat` wrapping, the baked taper slides into the middle of the quad
and the quad's own edges get cut off square — so `PlaceSheets` pins its offset to zero and the field's
own `PanU` is never applied. That left the *upload* as the only thing changing a patch's interior, and
the upload fires once per completed sweep. Baking on ticks rather than frames (which is right on its
own terms) then stretched a sweep from ~32 frames to 32 ticks, so the sole remaining source of motion
got **3.3× chunkier**. It shows in the colour first, because during a vanilla `Aurora` event the
palette transitions over 280 ticks and a sweep boundary every 32 of those quantises that glide into ~9
visible steps.

So the overlay keeps the **previous** completed sweep as well and draws each display twice: the old
field at `1-t` and the new one at `t`, where `t` is how far the sweep in progress has got. Under
additive blending the two draws sum, so this is an **exact** linear cross-fade — no custom shader, no
second UV set, no render texture. The displayed field then advances once per tick instead of once per
sweep, which is 32× finer and as fine as it can meaningfully be, since every input the field has is a
function of `TicksGame`. The two fields being blended are 32 ticks apart, i.e. ~0.6% of one feature
period, so it interpolates between two samples of a continuous field rather than dissolving between two
pictures. Costs: a second 147 KB texture, a second draw call per live display, and one more sweep of
display lag (~half a second) — all paid only while an aurora is up.

The **driver tint is pinned per sweep** for the same reason `time` is. Each slice used to bake the
colour as sampled on its own tick, so a tile assembled over 32 ticks carried a vertical gradient of
colours; against those 280-tick palette transitions that is ~11% of a full colour change between the
top and bottom of one tile.

Benchmarked under the .NET 8 JIT (RimWorld's Mono is slower — the `aurora_curtain_cost` probe carries
the real figure):

| slice | cost/frame | notes |
|---|---|---|
| 4 rows | ~0.25 ms | no cheaper than 6 — per-call setup dominates |
| **6 rows (shipped)** | **~0.24 ms** | the knee of the curve |
| 16 rows | ~0.60 ms | |
| 128 rows (whole field) | ~4.9 ms | a dropped frame |

That last row killed a design. The obvious implementation bakes the whole field on the aurora's first
frame so nothing unbaked is displayed — i.e. a guaranteed dropped frame every time an aurora begins,
at exactly the moment the player looks up. **It is also unnecessary**, which is the neat part: `new
byte[]` is zero-filled, so an unbaked row is `RGBA(0,0,0,0)`, and under additive blending zero
contributes *nothing*. An unbaked row is invisible, not garbage. Combined with the condition's own
hour-long fade-in, the field quietly fills itself in over the first ~22 frames while the aurora is
still too faint to see. So there is no priming pass. The corollary is that the texture is not
re-zeroed between events, which is strictly better: the second aurora of a playthrough is fully formed
immediately.

Resolution is the lever of last resort — 96² is a 1.8× saving and 64² a 4× one, both for no structural
change. `Resolution` and `RowsPerUpdate` are public so `AuroraCurtainCostProbe` times the values
actually shipped rather than a copy that can drift out of step with them.

**Two precision traps, both wrapped.** The drift clock is `TicksGame`, which grows without bound:

- `AuroraCurtain.DriftWrapTicks` (1,400,000) wraps the tick count **in integer arithmetic before the
  cast to float**. A float carries a 24-bit mantissa, so past 16,777,216 — about 278 in-game days — it
  cannot represent every integer, and an old colony's aurora would advance in visible jerks and then
  stop advancing at all.
- `AuroraCurtain.DriftWrapCycle` (840 lattice units) wraps the drift distance itself, and **840 is not
  arbitrary**. Every drift coefficient in the file is a multiple of 1/20 (0.35, 0.6, 0.2 for the
  ribbons; 0.25 envelope; 0.4 hue), so wrapping at 840 shifts each field by a multiple of 42 = 2·3·7 —
  divisible by every base period in the file (3, 7, 2, 2, 2, 3), with the octave doubling preserving
  the ratio. Every field therefore lands on an exact multiple of its own period and the wrap is
  bit-identical rather than a once-per-1.4M-ticks pop. `AuroraCurtainTests` pins both the constants'
  mutual agreement and the field's invariance across the wrap, so the pair cannot silently diverge.

**Hue agreement with §11.** `AuroraCurtain.DriverTintWeight` (0.3) is how far the driver condition's own
colour pulls the palette. Partial on purpose: at 1 the curtain would be a single hue again — the exact
failure being fixed, since vanilla's Aurora event cycles one colour at a time — while at 0 the ribbons
and the wash beneath them visibly disagree about the hue during an event.

**Testing.** `AuroraNoiseTests` and `AuroraCurtainTests` pin #42's acceptance criteria as assertions
rather than as taste: that the hue field reaches all three palette bands over one tile ("several
colours at once"), that `Wave` is ribbon-shaped rather than a uniform wash, that the field changes over
time, that the envelope leaves both lit and empty sky, and that a field assembled from several slices
is **byte-identical** to one baked in a single pass — the property the whole incremental refresh rests
on. `Wave` is also pinned tileable at both seams. `aurora_curtain` is the live strength probe
(`AuroraConditions.CurrentCurtainStrength`, shared with the patch), `aurora_curtain_cost` the Mono
timing, and `Tests/Scenarios/aurora_curtain.json` is a timelapse pair (off, then on) because this is a
temporal effect and a still A/B cannot show drift. It also pins the flag rule end-to-end: with the
curtain off, `aurora_tint` must read 0.18, and with it on, 0.075.

### 11b. The contour field, and why it is kept but not drawn (sketch — not implemented)

`Source/AuroraCurtain.cs` draws the **contour** where two counter-drifting fBm fields are equal. It was
§11a's first field and it is not what ships, but it is deliberately kept compiled, unit-tested and
rendered by `Tools/AuroraPreview` rather than deleted.

**Why it lost.** It is *physically honest and visually unrecognisable.* From directly overhead — which
is the only angle RimWorld's camera has — a real auroral arc genuinely is a flat wandering squiggle;
the vertical curtain everyone pictures is a side-on phenomenon we have no viewing angle for. The Godot
shader the technique came from recovered the iconic look by raymarching, which buys nothing here: there
is no view ray to smear along and no parallax to sell. Rendered, it reads as green smoke. §11a's
shipping field gives up on deriving the silhouette from a projection and simply authors it, accepting a
committed up-direction as the price.

**Why it is kept.** Two reasons, and the first is the real one.

1. **A world map is viewed from space, where an overhead contour is correct rather than merely honest.**
   Seen from orbit an aurora *is* a contour — the auroral oval, a band around the magnetic pole. So the
   contour field is the natural texture for a world-map auroral overlay, drawn at high latitudes on the
   globe. That would reuse the same pure core, the same noise primitive and the same palette, with a
   `WorldDrawLayer` in place of the map overlay. §11a's `AuroraFieldSpec` already expresses it; nothing
   about the field would need re-deriving.

2. It is the honest option should a "Realistic" aurora setting ever be wanted. **Deliberately not
   offered today**: a player picking "overhead view" from a radio button would reasonably conclude the
   aurora was broken and file a bug. The option only becomes meaningful once (1) exists, where the
   overhead view is not a compromise but the correct projection.

Note the Realistic/Cinematic presets deliberately carry only correlated aesthetic sliders — per-effect
toggles and mode enums (`EclipseMode`, `SunClockMode`) stay out of them by design, and a field choice
would too. That omission is intentional and should not be "fixed".

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

**Integration seam.** Until the moon-position (§6) and night-radiance (§7)
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

The product is simultaneously the classifier and the guard, which is what makes the vanilla census
come out right with no roof check and no defName list:

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
spared only by the luck of shipping Clear's palette.

### Modded weathers, and why the palette alone was not enough

§13 originally shipped with the palette rule as the *whole* classifier, arguing from the table above
that it needed no roof check, no biome check and no defName list. That argument was sound about
vanilla and wrong about the world. `Tools/WeatherAudit` — a checked-in dev tool that links the shipped
`WeatherDimmingMath` and walks every def on disk — put numbers on it: across **81 `WeatherDef`s and 65
`BiomeDef`s in vanilla plus 24 installed workshop mods**, the palette rule alone misclassified content
in both directions.

- **False positives.** The `saturation: 1.25` convention that spares vanilla's caves is a *vanilla*
  convention. Biomes! Caverns' `BMT_Calm` and MultiFloors' `MF_UndergroundWeather` are cave
  environments shipping overcast-shaped palettes, rated **1.00** and **0.71**. Anomalies Expected's
  `AE_BloodLakeWeatherClear` is a third.
- **False negatives.** Alpha Biomes' `AB_ForsakenRainyNight_Alternate` is a rainstorm (`rainRate` 1.0)
  whose day palette is only partway to overcast, rated **0.43** — a visibly wet sky dimmed as though
  half-clear.

The fix is emphatically *not* a cleverer palette rule. Every palette-only discriminator tried against
that census failed the same way; the most promising, day-versus-night contrast (an enclosed map has no
diurnal variation, an open sky does), traded those three false positives for **five** real modded
weathers — `AB_RedFog`, `CrimsonDeathPallSTNL`, `REDS_EerieClouds`, `VPE_RadioactiveFog`,
`VGE_ToxicDustCloud` — which ship flat or even inverted day/night palettes. That is a worse deal, and
finding it out is the reason the audit exists.

What actually resolves it is noticing that two different questions had been collapsed into one.
"Is this palette a cloud deck?" is a question about a `WeatherDef`. **"Is there a sky over this map at
all?" is a question about the map**, and the map answers it directly:

```csharp
// WeatherDimming.HasSky — either condition means no sky, so no dimming
biome.disableSkyLighting                                  // vanilla's own "nothing overhead" flag
|| biome.baseWeatherCommonalities.Count(c => c.commonality > 0) < 2
```

The second clause is the interesting one: **a biome that offers fewer than two possible weathers can
never change weather**, so its single palette is the map's fixed environment rather than a deck that
rolled in.

It is worth being precise about what that rule does and does not do, because the first draft of this
section overclaimed it as a clean partition and a **live harness run caught the error**. It is not.
Vanilla's `Undercave` biome offers *two* weathers, not one: it declares `Undercave` and inherits
`Underground` from `Biome_Underground`, and RimWorld's XML inheritance
(`XmlInheritance.RecursiveNodeCopyOverwriteElements`) recurses into the shared
`baseWeatherCommonalities` node and **appends** rather than replacing. So Undercave sits at 2, exactly
like the open-air Duskwood, and the rule treats both as having a climate. The audit tool had been
resolving inheritance shallowly and reported Undercave as 1, which made the partition look clean; the
scenario probe on a live Undercave map read a nonzero dimming and disproved it.

What makes the rule safe is therefore not the count but *which biomes can actually do harm*. A skyless
biome only dims wrongly if it can roll a weather whose palette classifies above 0, and across all 65
biomes in the census:

- every biome that **could** have dimmed wrongly offers exactly one weather and is caught —
  `BMT_CrystalCaverns` (1.00), `BMT_EarthenDepths` (1.00), `AE_BloodLakeBiome` (1.00),
  `AE_ChristmasTreeBiome` (1.00), `MF_BasementBiome` (0.71);
- every skyless biome **above** the threshold offers only weathers that already classify to exactly 0
  — Undercave and `UV_SpaceUndercave` can roll `Undercave` or `Underground`, both of which keep the
  clear family's saturation of 1.25.

`Tools/WeatherAudit` prints that as a `worst` column per biome plus a short boundary list, so the
claim is re-checkable after a mod-list change rather than resting on this paragraph. Being wrong is
also one-directional and cheap: a hypothetical open-air biome with a single eternal weather simply
gets no dimming, leaving its author's palette exactly as authored.

A third condition was measured and **rejected**: `generatesNaturally == false` would additionally
catch Undercave and every vanilla pocket map, but it also catches Deep Mining's
`DMSE_ImpactCraterBiome`, an open-air crater with `Clear`/`Rain`/`DryThunderstorm` that genuinely wants
dimming. It buys only biomes that were already harmless and costs a real one.

Counting *nonzero* commonalities rather than entries is load-bearing, not fussiness — Biomes! Caverns
lists vanilla's `Rain`/`FoggyRain`/`DryThunderstorm` on its cavern biomes at commonality 0 precisely
to suppress them, and an entry count would read those caverns as having a climate.

The false negatives are then closed by a second, independent line of evidence at the def level:
**precipitation implies a deck.** Rain, snow and blown sand all require something overhead to fall
from, so any nonzero rate settles *whether* there is a deck outright — the classifier takes the `max`
of the palette verdict and this one. It is categorical rather than proportional because *how hard* it
is coming down is already a separate axis (the intensity band below), and it is provably a no-op on
vanilla: every precipitating vanilla weather already scores a full 1.00 on the palette rule.

So the classifier is three layers, and still not a defName list anywhere:

| layer | question | where |
|---|---|---|
| `HasSky` | is there weather over this map at all? | `WeatherDimming` (map-level veto) |
| `PaletteOpacity` | how overcast does the palette look? | `WeatherDimmingMath` |
| `PrecipitationEvidence` | is anything falling out of the sky? | `WeatherDimmingMath` |

**The residue, and the escape hatch.** Seven defs still land strictly between 0 and 1 on a map that
has a climate: `VEE_Inferno`/`VEE_Swelter` (0.34, heat events), `AB_ForsakenNight_Alternate` (0.43),
`VGE_ToxicDustCloud` (0.52), `VPE_RadioactiveFog` (0.53), `AB_ForsakenThunderstorm_Alternate` (0.71),
`VREA_PsychicStorm` (0.92). The continuous ramp is **kept** for these rather than snapped to 0/1 past
a threshold, and now with data behind that choice: those seven really are a continuum of partial
decks, the two hard misclassifications that used to sit among them were both enclosed maps and are now
closed structurally, and no rule can tell a mod author's "hazy heat shimmer, do not darken" from their
"thin high cloud, do darken". The worst case is a bounded ~6–17% dim on content whose own palette is
already ambiguous.

For that last class there is `CelestialLighting.WeatherCloudDeck`, a `DefModExtension` carrying a
single `<opacity>` in [0,1] that overrides the classifier for one def. A defName list of ours would
answer today's seven and then rot — an entry needed for every future mod, invisible to the authors it
is about, silently contradicting whatever they later changed their palette to. The extension inverts
all three: the answer lives next to the def it is about, we carry no list, and anything without it
keeps classifying automatically. It is an escape hatch for where data runs out, not a replacement for
the data-driven premise.

Worth keeping in proportion throughout: §13 never writes `SkyTarget.glow`, so even a badly
misclassified modded weather can only change what the sky *looks* like — never plant growth, solar
output or pawn vision.

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
  and so a mild unrequested dim. Bounded, and the alternative (a hard threshold) is more brittle —
  see the modded-weather section above for the census that settled this.
- The audit tool is not a def loader and does not pretend to be: it ignores `PatchOperation`s,
  `Inherit="false"` and cross-mod load order, so a def that only becomes misclassified after another
  mod patches it will not show up. Its job is to surface candidates worth looking at in game, not to
  be authoritative.

**Conflict risk.** Low, and lower than every other glow-touching subsystem here: we write only
`SkyColorSet` fields plus a shadow-alpha multiply, so a mod that reads `SkyManager.CurSkyGlow`
(Dub's Skylights, solar output, plant growth) sees nothing different under any weather. The one
sharp edge is internal: `colors.sky` is assigned straight to `MatBases.LightOverlay.color`, whose
**alpha** is how much of the lighting overlay is drawn at all — vanilla writes `(1,1,1,0)` to switch
it off for `disableSkyLighting` biomes. So the scale is RGB-only; a naive `color * factor` (Unity
scales all four channels) would fade the darkening overlay *out* and make heavy weather render
brighter, the exact opposite of the intent.

## 15. Eaves: roofed cells that are not indoors (`EavesMath` / `EaveShadowGrid` / `Patch_ShadowRoofInvalidation`)

**Problem.** Two of our subsystems ask "is this cell indoors?" and both answered with
`map.roofGrid.Roofed(cell)`, which is too coarse in opposite directions.

§4's shadow mesh never consults the roof grid at all — vanilla's only shadow casters are *edifices*,
so a porch roof, a lean-to, an overhang, or the eave that oversails a wall casts nothing, and
sunlight lands on the porch floor as though the roof above it were not there. Under vanilla's narrow
shadow-angle range this was easy to miss; with §1 raking shadows through every compass direction
across the day and the season, it is a hole you cannot stop seeing.

§7b's occlusion has the mirror-image bug: it treats *every* roofed cell as sealed, so the same porch
goes pitch black at noon while standing wide open to the sky on three sides. That was
the most conspicuous artifact the feature shipped with.

**Approach.** Both want the same finer distinction, so it is stated once, purely, in `EavesMath`. A
roofed cell is either **enclosed** — part of a room that holds its own temperature, i.e. genuinely
inside a building — or an **eave**: roofed, but breathing outdoor air. `Room.UsesOutdoorTemperature`
is the game's own answer to that question (it decides whether a room heats, whether rain reaches it,
whether pawns count as sheltered), so keying off it means our notion of "indoors" cannot drift from
the one the simulation already uses. `IsEave` and `IsEnclosed` are exact complements within roofed
cells, pinned by a test — a gap there would mean a cell that neither casts nor occludes. (The two
halves turned out to want *nearly* the same distinction, diverging on exactly one input; see the
thick-roof bullets below.)

- **Shadow half** (gated by the "Eave shadows" toggle). `EaveShadowGrid` resolves an effective
  caster *height* per cell — the edifice's `staticSunShadowHeight`, or `max(that, 1.0)` on an eave,
  1.0 being what vanilla's Wall and Door both declare. `Patch_ShadowMeshPerimeter`'s reimplemented
  `Regenerate` reads those floats instead of `Building`s. The substitution is provably equivalent to
  vanilla's own tests when the toggle is off, because every neighbour test only runs once the centre
  cell's height is already `> 0`, so a null neighbour's 0 satisfies `< centreHeight` for exactly the
  reason `building == null` did.
- **Occlusion half** (ungated). `Patch_IndoorSkyOcclusion.BlocksSky` now asks `EaveCells.Encloses`
  instead of the raw roof grid. Deliberately not behind the eave toggle: that flag turns a new
  *effect* on and off, whereas this is a correction to a question §7b was already asking wrongly.
- **Thick roof is never an eave**, and that veto is load-bearing rather than tidy.
  `UsesOutdoorTemperature` is `TouchesMapEdge || OpenRoofCount >= 25%`, and a cave system that reaches
  the map edge — the common case — satisfies the first disjunct for its entire interior. Without the
  veto every cell of such a cave would classify as an eave: §7b would stop occluding it (a cavern lit
  at 61% of the sky, precisely the bug §7b exists to fix) and §15b would paint its whole floor as an
  open-air porch. There is no sky under a mountain in any case, which is the same exception vanilla
  itself makes in `SectionLayer_LightingOverlay`.
- **…but a mountain roofline still casts.** The veto above was originally stated once and consumed by
  all three of the shadow, occlusion and shade halves, and that overreached. At the boundary between
  mined-out mountain and built structure — the commonest shape in a mountain base — one continuous
  roofline runs from `RoofRockThick` to `RoofConstructed` with no visible seam, and the shared veto
  made the constructed half throw a full shadow while the mountain half threw none, every frame the
  sun was up. So `EavesMath` states two predicates over the same four inputs: `CastsRoofShadow`
  (§15's mesh) and `IsEave` (§7b and §15b), differing *only* in the `thickRoof` term, with the
  relationship `IsEave == CastsRoofShadow && !thickRoof` pinned over the whole input space by a test.
  The veto guards what is *under* the roof — is there sky above this cell? — not what the roof's
  *edge* does to the sunlight beside it, and those turned out to be two questions.

  Two consequences, both accepted knowingly:

  - **A mountain casts exactly one wall of shadow, no more.** `Formulas.ShadowCasterAlphaByte` packs
    the height into a vertex-colour *byte* and the shader is `position += colour.a * _CastVect`, so
    alpha 255 = 1.0 = one wall of extrusion, and there is no room in the channel to say "cliff". A
    mountain that under-throws reads as unremarkable; a mountain that throws nothing beside a shed
    that throws fully reads as a bug. The defect being fixed is the discontinuity, not the length.
  - **Admitting a whole cavern costs no pixels and, now, no vertices.** Only a caster blob's
    *trailing* perimeter renders (the leading-edge skirts are backface-culled — see 15b), and a
    cavern cell's neighbours are either more cavern at the same height or solid rock, which vanilla
    already declares at 1.0; only cells where a mountain roofline actually meets open sky gain a
    quad. The footprint quad was the remaining per-cell cost, so `Patch_ShadowMeshPerimeter` now
    skips it — along with the whole cell — when no neighbour is shorter. That is sound precisely
    because the footprint is invisible (15b below) and because the four skirt blocks that index back
    into it are exactly the ones that cannot fire under that condition. It reorders work rather than
    adding it: the four neighbour comparisons were already being made, one per skirt block.

  Pinned live by `Tests/Scenarios/eaves_thick_boundary.json`, which reads *both* counts — `eave_cells`
  9 and `roof_shadow_cells` 18 over one 18-cell roofline — because the eave count is correct at such a
  boundary and the caster count was the one that was wrong.
- **Invalidation.** `SectionLayer_SunShadows`'s constructor sets `relevantChangeTypes =
  MapMeshFlagDefOf.Buildings` and nothing more — it never had a reason to care about roofs. Now that
  roof state feeds a caster height it does, and `RoofGrid.SetRoof` dirties only `Roofs`.
  `Patch_ShadowRoofInvalidation` is a Postfix on that constructor OR-ing `Roofs` into the
  subscription. Widening a subscription can only cause extra regenerates, never suppress one.

### 15b. A roof shades the ground under it (`EaveShadeMath` / `SectionLayer_EaveShade`)

**Problem.** Shipping the shadow half revealed a hole in vanilla's shadow mesh. A caster never shades
its **own** cell: `SectionLayer_SunShadows` emits a flat footprint quad whose four vertices carry
alpha 0, and that alpha is both the displacement the shader applies *and* what the fragment is drawn
at — so the footprint renders fully transparent. Nobody ever noticed, because before §15 every caster
was an edifice and a wall's sprite covers whatever colour the ground under it is. Give the roof
itself a caster and the hole becomes the visible thing: a porch throws a shadow across the ground
beside it while the porch floor is lit as though the roof above it were not there.

It cannot be closed inside that mesh. Raising the footprint's alpha to make it opaque is the same
number that makes the shader push the quad a whole shadow-length away, off the cell it was meant to
shade — the two meanings share one channel.

**Measured**, live A/B at 15:00, latitude 45, clear sky, over a 9x9 roof slab on concrete, against
open sunlit ground at 1.000:

| region | value | |
|---|---|---|
| open sunlit ground | 1.000 | |
| under the roof | 0.605 | vanilla's roofed sky cover (`1 - 100/255`), no shadow of any kind on it |
| the cast shadow on open ground | 0.742 | luminance of vanilla's Clear-day shadow tint (0.718, 0.745, 0.757) |
| the rim, where that shadow laps the roof's own cover fade | 0.581 | the two multiplied |

Both vanilla numbers reproduce to three decimals, which is what pins the model to the arithmetic the
renderer is really doing. The porch reads as a pale square with a dark edge hung off its shadow side:
lighter than its own shadow exactly where the two touch.

**Approach.** An eave cell takes the same shadow multiply the ground beside it takes — no more, no
less. Not a number invented for porches: it is the footprint shading vanilla's own mesh would already
have drawn if that quad were not structurally transparent, carried by the only channel that can carry
it. The roof's separate cost in *sky* stays exactly vanilla's cover; nothing about §7b changes.

- `SectionLayer_EaveShade` is a map draw layer of our own, registered for free the same way §9's
  desaturation layer is (`Verse.Section`'s constructor instantiates every non-abstract `SectionLayer`
  subclass, so declaring the class is the registration). It bakes one bit per cell — eave or not —
  into vertex alpha, at `AltitudeLayer.Shadows`, the altitude vanilla's own sun shadows use, because
  this *is* one. Deliberately **not** averaged across neighbouring cells the way §9's wash is: a
  roofline is a physical edge and the shadow it throws has a hard one, so softening this side of it
  would leave a bright lip along the boundary — a smaller copy of the artifact being removed.
- `EaveShadeOverlay` owns the material (`ShaderDatabase.Transparent`, ours rather than pooled).
  Alpha-blending **black** at alpha `a` is `scene * (1 - a)`, a multiply, which is what lets the
  shade, the sun shadow and the sky cover compose by plain multiplication and in any draw order.
  `MatBases.SunShadow` is not reusable here: its shader displaces vertices by the shadow vector
  scaled by vertex alpha, which is the very coupling that makes a caster unable to shade its own cell.
- `Patch_EaveShade` (Postfix on `SkyManager.SkyManagerUpdate`) writes that alpha once per frame as
  `1 - luminance(MatBases.SunShadow.color)`. Reading the finished material rather than re-deriving
  depth from sun elevation is the point: every shadow feature we own — §1's elevation ramp, the
  angular-size penumbra, §13's weather softening, the moon's night handoff — reaches the screen
  through `GenCelestial.CurShadowStrength`, which `SkyManager` lerps that colour by. The eave can
  therefore never drift from the shadow it matches, including for features not written yet.

**It cannot go pitch black, by construction.** The multiply is bounded below by vanilla's own shadow
palette: the darkest tint any vanilla weather declares is Clear's 0.740, most are 0.92 or lighter,
and shadow strength only ever lerps that tint back *toward white* as the sun drops. So the deepest an
eave can reach is `0.608 x 0.740 = 0.449` of open sunlit ground, in full midday sun; at dusk, under
overcast and all night the multiply is at or near 1 and this contributes nothing at all, leaving the
porch at exactly the roof cover players already see. Nothing here can reach the fully-occluded
interior (0.0) §7b applies to a sealed room — an eave is never classified as interior in the first
place. `EaveShadeMathTests` asserts that floor rather than trusting it.

Gated by the same "Eave shadows" toggle as the caster half: off means no layer drawn and the material
held at zero, so the A/B is a true no-op either way round.

**Why re-implement rather than defer to Perspective: Eaves.** That mod (Owlchemist, continued by
Mlie; MIT) had the load-bearing insight — `UsesOutdoorTemperature`, not `Roofed`, is the real test —
and this subsystem restates it. Three reasons we do not simply call into it:

1. **We already break it, unavoidably.** It expresses the rule by transpiling
   `SectionLayer_SunShadows.Regenerate` to swap `EdificeGrid.InnerArray` for its own adjusted copy.
   `Patch_ShadowMeshPerimeter` is a Prefix that replaces that entire method (it has to — §4 adds a
   shadow face vanilla never builds, and Harmony cannot append to an already-`FinalizeMesh`'d
   submesh), so its transpiled body never runs. No load order fixes that: one of the two is always
   dead. Worse, the half of it we *don't* touch keeps working, leaving a silently inconsistent state
   in which porches are exempt from the indoor mask but still cast no shadow.
2. **Its shape is O(map) per section.** `RoofShadows.GetAdjustedList` copies the whole edifice array
   and walks the whole roof grid on every section regenerate — on a 275x275 map a full-map roof
   change is ~121 sections × ~75k cells of room queries and ~36 MB of transient arrays. A section
   only ever draws its own cells plus a one-cell skirt, which is all `EaveShadowGrid` resolves.
3. **Its invalidation fix is broader than the problem.** It transpiles `MapDrawer.MapMeshDirty` call
   sites inside both `RoofGrid.SetRoof` and `Building.SpawnSetup` — two hot, widely-patched vanilla
   methods rewritten to fix a subscription. We change the subscription instead, and `SpawnSetup`
   needs nothing at all because it already dirties `Buildings`.

One deliberate behavioural divergence: it substitutes a fixed 1.0 dummy caster into any roofed cell
whose edifice is not exactly 1.0, which silently *shortens* a modded caster taller than a wall (a
watchtower, a battlement) wherever a roof happens to cover it. We take the max, so roofing something
can only ever add shadow.

**Known limitation** (shared with Perspective: Eaves, not introduced here). A cell's eave status
depends on its *room*, and enclosing a room flips cells nowhere near the wall that closed it —
sealing a doorway stops an entire porch being a porch, but only the doorway's section is dirtied.
Chasing that means hooking region/room recalculation, a far more invasive and far hotter path than
this is worth; any later roof or building edit in the affected section resolves it.

**Conflict risk.** This is the one place in the mod where we declare an outright incompatibility
rather than a load order: `About.xml` lists `Mlie.PerspectiveEaves` and `Owlchemist.PerspectiveEaves`
under `<incompatibleWith>`, because for the reason in (1) the two genuinely cannot both work. Beyond
that the surface is small — the constructor Postfix touches a public field on a type nothing else has
reason to patch, and the occlusion change is confined to a predicate inside our own postfix.

**Provenance.** Perspective: Eaves is MIT and was read as such (decompiled from the user's installed
1.6 copy; also public at github.com/emipa606/PerspectiveEaves). What was taken is the *idea* that
`Room.UsesOutdoorTemperature` is the right predicate. No code was copied, and the two
implementations share neither structure nor mechanism.

## 16. Section-layer invalidation: what one dirty flag now costs (§7b / §9 / §15)

**Problem.** Four separate decisions — §9's desaturation layer, §15b's eave-shade layer, §15's
widening of the sun-shadow subscription, and §7b's postfix on vanilla's lighting overlay — each
argue their own case correctly in their own file header, and not one of the four mentions the other
three. What none of them can show is the *total*: how many section layers now regenerate when one
map-mesh dirty flag is raised. That number only exists in the interaction, so a reader has to open
four files and hold all four subscriptions in their head to see it. This section is where it is
written down instead (issue #10).

Nothing here argues for removing any of the four. Each is individually necessary; the point is that
the compound cost has an owner and a number.

### How a dirty flag becomes work

`Verse.Section.TryUpdate(CellRect view)` is the path a live edit takes (decompiled 1.6). Three
properties of that loop drive the whole cost picture, and all three are easy to get wrong from
memory:

- **It does not check `Visible`.** `RegenerateAllLayers` and `RegenerateDirtyLayers` both skip
  invisible layers; `TryUpdate` does not — it tests only `(dirtyFlags & layer.relevantChangeTypes)`
  and calls `Regenerate()`. So a layer of ours whose feature toggle is **off** still pays full
  regenerate cost on every relevant dirty flag. Only `DrawLayer` consults `Visible`.
- **Off-screen sections do not pay now.** The regenerate is gated on `bounds.Overlaps(view)`;
  a dirtied section outside the camera is only flagged (`anyLayerDirty`) and regenerates later, in
  `DrawSection`, if it ever comes on screen. A zone-roof order across 30 sections therefore costs
  only the sections currently on camera — the rest is deferred or never paid at all.
- **The unit of work is a whole section**, not the cell that changed: 17×17 = 289 cells, 9 vertices
  each = 2601 vertex colours rebuilt and re-uploaded per layer. And `MapDrawer.MapMeshDirty` sets
  `regenAdjacentCells` for `Roofs`/`Buildings`/`FogOfWar`, so one cell edit on a section boundary
  dirties up to four sections.

### Who subscribes to what

Vanilla column verified by decompiling every non-abstract `SectionLayer` in 1.6's
`Assembly-CSharp.dll` (not from memory — the counts in issue #10 were low, see below). `Section`'s
constructor instantiates *all* of them except `SunShadows` (dropped when
`map.info.disableSunShadows` or the biome disables shadows), `EdgeShadows` (biome) and
`PollutionCloud` (no Biotech) — including the Odyssey layers, DLC or not.

| Layer | `relevantChangeTypes` | Owner |
|---|---|---|
| `SectionLayer_LightingOverlay` | `Roofs \| GroundGlow` | vanilla (our §7b postfix rides its `Regenerate`) |
| `SectionLayer_IndoorMask` | `Buildings \| Roofs \| FogOfWar` | vanilla |
| `SectionLayer_GravshipHull` | `Buildings \| Terrain \| Things \| Roofs` | vanilla (Odyssey) |
| `SectionLayer_Darkness` | `GroundGlow` | vanilla |
| `SectionLayer_BuildingsDamage` | `BuildingsDamage \| Buildings` | vanilla |
| `SectionLayer_EdgeShadows` | `Buildings` | vanilla |
| `SectionLayer_SubstructureProps` | `Terrain \| Buildings` | vanilla (Odyssey) |
| `SectionLayer_SunShadows` | `Buildings` **→ `\| Roofs`** | vanilla, widened by `Patch_ShadowRoofInvalidation` (§15) |
| `SectionLayer_NightDesaturation` | `Roofs \| GroundGlow` | ours (§9) |
| `SectionLayer_EaveShade` | `Roofs \| Buildings` | ours (§15b) |

This mod ships **no `MapMeshFlagDef` of its own** and raises no flag on a schedule: every section
regeneration it causes is downstream of a vanilla edit. It did until §3's across-map shadow tilt was
removed — that feature owned a private `CL_SunShadowAxis` flag and dirtied the whole map roughly 720
times per game day to keep its bake fresh, which made it the one term in this ledger that was not
somebody else's edit. See §3 for why it went.

Per flag, layers that regenerate in a dirtied on-screen section:

| Flag dirtied | Vanilla | With this mod | Ours |
|---|---|---|---|
| `Roofs` | 3 | **6** | `NightDesaturation`, `EaveShade`, `SunShadows` (widened) |
| `GroundGlow` | 2 | **3** | `NightDesaturation` |
| `Buildings` | 6 | **7** | `EaveShade` |
| `Roofs \| GroundGlow` together | 4 | **7** | all three above |

Issue #10 tabulated this as 1 → 4, 1 → 2 and 1 → 2. The *added* layers were right; the vanilla
baseline was not. The real multiplier on `Roofs` is 2x, not 4x, and on `Buildings` it is +17%.

### What actually raises these flags

The frequency assumption matters more than the multiplier, and it is where the surprise is:

- `RoofGrid.SetRoof` → `Roofs`. Player-initiated, infrequent. This is the case everyone pictures.
- **`GlowGrid.DirtyCell` → `Roofs` *and* `GroundGlow`, both.** It is called per affected cell from
  `RegisterGlower`/`DeRegisterGlower`/`LightBlockerAdded`/`LightBlockerRemoved`. So every lamp
  switching on or off, every fire starting or dying, every sun lamp cycling at dawn raises `Roofs`
  across the sections it covers. `Roofs` is not a rare flag; it is a lighting flag that also
  happens to fire on roof edits.
- `Building.SpawnSetup`/despawn → `Buildings`.
- Ours, at settings-change frequency only: `IndoorOcclusionRedraw` → `WholeMapChanged(GroundGlow)`
  and `EaveShadowRedraw` → `WholeMapChanged(Buildings)`, both change-detected so an open settings
  window does not fire them at 60 Hz.

The consequence worth writing down: because `GlowGrid.DirtyCell` raises `Roofs`, our widening of
`SectionLayer_SunShadows` means **the sun-shadow mesh now rebuilds when a lamp toggles**, which can
never change it. That is the one strictly-wasted regenerate the fan-out introduces, and it is a
larger effect than the `Buildings` subscription issue #10 flagged as the narrowing candidate. Both
are recorded here rather than acted on — see the measurement below for why.

### Measured

Issue #10's cost estimate was operation counting and said so. This is not: `SectionRegenerateTimingProbe`
times one layer's `Regenerate()` on a live map — the same call `TryUpdate` makes — over four sections
that contain roof, 2 warmup + 10 timed runs each, and reports the mean in microseconds. The scenario
is `Tests/Scenarios/layer_regen_timing.json`.

It has to be a live measurement. The three dominant terms are the glow-grid read, the region/room
read and the Unity mesh upload inside `FinalizeMesh`, and none of the three exists outside a running
game — an offline benchmark could only have timed the float arithmetic around them, which is not
where the cost is. Numbers below are the mean over two full runs, which agreed to within 3%.

| Layer | µs per section regenerate | On which flags |
|---|---|---|
| `SectionLayer_NightDesaturation` (ours, §9) | **271** | `Roofs`, `GroundGlow` |
| `SectionLayer_LightingOverlay` **with** our §7b postfix | **158** | `Roofs`, `GroundGlow` |
| `SectionLayer_LightingOverlay` with §7b switched off | 63 | `Roofs`, `GroundGlow` |
| `SectionLayer_GravshipHull` (vanilla, Odyssey active) | 57 | `Roofs`, `Buildings`, … |
| `SectionLayer_IndoorMask` (vanilla) | 30 | `Roofs`, `Buildings`, `FogOfWar` |
| `SectionLayer_SunShadows` (vanilla, our §4/§15 prefix) | 26 | `Buildings`, **`Roofs`** |
| `SectionLayer_EaveShade` (ours, §15b) | **13** | `Roofs`, `Buildings` |
| `SectionLayer_Darkness` (vanilla) | 0.02 | `GroundGlow` (returns immediately without Anomaly) |

Per `Roofs` flag, per dirtied on-screen section:

| | µs | |
|---|---|---|
| vanilla | 150 | overlay 63 + indoor mask 30 + gravship hull 57 |
| with this mod | 555 | + desaturation 271, + §7b's 95, + sun shadows 26, + eave shade 13 |

**3.7x, not 4x-by-layer-count — and the layer count was the wrong thing to look at.** Two thirds of
everything we add to a roof edit is one layer, and it is not either of the ones the issue suspected:

| Our share of the +405 µs | |
|---|---|
| `SectionLayer_NightDesaturation` | 271 µs — **67%** |
| §7b's postfix on vanilla's lighting overlay | 95 µs — 24% (it costs more than the layer it postfixes) |
| `SectionLayer_SunShadows`, from the `Roofs` widening | 26 µs — 6% |
| `SectionLayer_EaveShade` | 13 µs — **3%** |

Two further measurements, each taken with the feature's own toggle switched off:

- Desaturation off: **271 µs**, unchanged. Eave shadows off: 13 µs, unchanged. This is the
  `Visible`-is-not-checked property above, confirmed on a live map rather than inferred from the
  decompile — **a player who turns these features off pays the full cost of both layers anyway.**
- §7b off: vanilla's lighting overlay drops from 158 µs to 63 µs. Our occlusion postfix is 2.5x the
  cost of the entire vanilla layer it postfixes.

In wall-clock terms a roof edit dirties its own section plus the sections of the eight adjacent
cells — at most four distinct sections — so roofing a 10x10 room costs on the order of 2.2 ms with
this mod against 0.6 ms without, once, in the frame the camera is looking at it, and nothing at all
for sections off-screen. That is a real regression and not a hitch anybody will see. The frequency
finding above matters far more than the roof case: the same 555 µs is paid per section on **every
lamp toggle**, because `GlowGrid.DirtyCell` raises `Roofs` alongside `GroundGlow`.

### The other half of the ledger: what the layers cost when they are *idle*

Everything above measures a **regenerate**, which happens only when a section is dirtied. It misses
the cost the same two layers carry every frame regardless, and that turned out to be the one a player
actually feels — because it is paid continuously rather than on an edit.

Both of our map-wide overlays split the same way vanilla's lighting does: the per-cell part is baked
into the mesh, the map-wide strength lives in the shared material's alpha. That split is right, and it
has a blind spot. `MapDrawLayer.DrawLayer` gates only on `Visible` and `subMesh.disabled` — it knows
nothing about the material — so a layer whose alpha is zero still submits one `Graphics.DrawMesh` per
on-screen section per frame. The GPU then runs a viewport-sized transparent blend that writes no
pixels, which on a fill-rate-bound machine is the larger half of the waste.

Each layer is transparent for half of every day, in opposite phases:

| Layer | Transparent when | Why exactly zero, not merely small |
|---|---|---|
| `SectionLayer_NightDesaturation` (§9) | all of daylight | `PurkinjeMath.PurkinjeFactor` is an `InverseLerpClamped` that reaches 0 at `OnsetGlow` |
| `SectionLayer_EaveShade` (§15b) | moonless night, blacked-out sky | `EaveShadeMath.ShadeAlpha` of a white `MatBases.SunShadow` tint |

Measured live (`Tests/Scenarios/idle_layer_draws.json`, `SectionLayerDrawCountProbe`, 8 sections on
screen at zoom 14, mean over 91 frames):

| | before | after |
|---|---|---|
| wash submissions/frame at noon | **8** | **0** |
| wash submissions/frame at midnight | 8 | 8 |
| eave-shade submissions/frame, moonlit midnight | 8 | 8 |
| eave-shade submissions/frame, new moon | **8** | **0** |
| all section-layer submissions/frame | 174 | 166 |

So the two layers were 16 of 174 submissions — 9% of everything the map draws — and half of that was
always dead. It scales with visible sections, not with map size: the same run zoomed out costs
proportionally more.

**The fix is a `DrawLayer` override, deliberately not a clause on `Visible`,** and the distinction is
load-bearing rather than stylistic. `Section.TryUpdate` does not consult `Visible` before calling
`Regenerate`, but it **does** clear the layer's `Dirty` flag afterwards, and §9's `Regenerate` discards
its mesh when `!Visible`. Gating visibility on brightness would therefore let a daytime lamp toggle
throw the wash away and mark the layer clean, leaving the map blank at dusk until something dirtied it
again — the same "the setting did nothing" failure `NightDesaturationRedraw` exists to prevent, but
fired by the clock instead of by a click. Skipping at the draw leaves the bake, the `Dirty` bookkeeping
and `Visible` all untouched.

It is also not the flicker-across-dusk test both layers' `Visible` comments reject. That one is a
threshold on how dark it is; this is "the alpha we already wrote is exactly zero", where drawn and
skipped are the same pixels by construction — the identical test `SetMapWash` already short-circuits
on before touching the material.

**What this does not fix.** The bake is still time-of-day-blind: §9's wash alphas come from local glow
with `ignoreSky: true`, so a daytime lamp toggle still pays the full per-section regenerate above for a
mesh that will not be drawn. Skipping *that* by time of day would trade a per-edit cost for a whole-map
rebuild at dusk, which is a worse shape of cost, so it stays.

**A premise this measurement corrected.** The eave shade was expected to be idle all night, and it is
not — with a moon up, eaves genuinely shade moonlight, and `shade_draws` stays at 8 through a moonlit
midnight. Its saving is real but narrower than §9's: moonless nights, overcast, and blacked-out skies
only. The new-moon arm of the scenario exists to keep that branch proven rather than assumed.

### Verdict, and what is deliberately not changed

- **`SectionLayer_EaveShade` keeps its `Buildings` subscription.** Issue #10 named it as the
  narrowing candidate on the reasoning that its trigger is broad and its payoff rare, which is true
  — and measured, it is 13 µs, 3% of what we add, the cheapest layer in the mod. Dropping
  `Buildings` would reintroduce "walling in a porch leaves it drawn as a porch" (§15's known
  limitation, in its worst form) to save a number indistinguishable from noise. Not a trade worth
  making.
- **`SectionLayer_NightDesaturation` is where the cost actually is**, and there are two independent,
  behaviour-preserving fixes available, both recorded here rather than taken in the same change that
  documented the problem: (1) `WashAt` calls `GroundGlowAt` **nine times per cell** — once as the
  cell, eight more as each neighbour's neighbour — which is 2601 glow-grid queries per section where
  361 into a reusable (17+2)² scratch grid would answer identically; vanilla's own
  `GenerateLightingOverlay` walks the vertex lattice precisely to avoid that. (2) An early return
  when `!Visible` would drop the layer to nothing for every player who has the feature off, since
  `TryUpdate` will not do it for us.
- **§7b's 95 µs postfix** is the second-largest term and belongs to the companion issue about the
  augmented lighting overlay, now with a number attached.

### The flag we used to raise ourselves (§3, issues #11 and #26)

Everything above is about work vanilla's own edits drag us into. For a while there was one term that
was not: `CL_SunShadowAxis`, a private `MapMeshFlagDef` that `MapComponent_SunShadowAxis` raised
whenever the shadow axis drifted past 0.5° from the one the meshes were baked against — ~720 times
per game day, ~0.7/s at normal speed and ~2.2/s at 3×, regenerating `SectionLayer_SunShadows` for
every section the camera could see. It has been removed along with the across-map tilt it served
(§3), so **this mod no longer schedules any section regeneration of its own.**

The per-section numbers are worth keeping, because they are the reason the removal was not obvious
from this table alone. With the bake, `SectionLayer_SunShadows` measured **29.1 / 29.6 µs over two
runs against the 26 µs above** — the alpha multiply added ~3.4 µs, +13%, and the other six layers
reproduced to within a few percent in the same runs, so that was a like-for-like delta. At 9–34
visible sections that is 0.26–1.00 ms per rebake, or **0.012–0.037 ms/frame amortised**, which ranks
it well below §9's 271 µs and §7b's 95 µs in the share-of-cost table.

**And yet it dominated the live profile.** That is the lesson: every other row in this ledger is paid
only when something in that section actually changes, and in a settled colony almost nothing does.
The tilt was the only feature dirtying sections on a clock, forever, so its amortised average *was*
its real cost while everyone else's was near zero. A per-section cost table ranks by how expensive a
regenerate is; it says nothing about how often one is provoked. Rank by provocation too.

One number did *not* move when the feature came out: re-running `layer_regen_timing` after the
removal, `SectionLayer_SunShadows` reads **28.9 µs**, statistically indistinguishable from the
29.1 / 29.6 µs measured *with* the bake. That is the expected result — the bake was one float
multiply per caster vertex — and it is the point restated as a measurement: the tilt never cost much
per regenerate, it cost by causing regenerates. (The other six layers in that same run also read well
off the table above, so it is not a like-for-like capture and the table is left as recorded.)

For the record, the design it replaced was worse again on both axes: `Patch_ShadowTilt`'s per-draw
prefix measured **0.300 ms/frame average, 1.590 ms max — 7.73% of a 3.879 ms frame, two thirds of the
mod's entire CPU cost** (issue #23) at ~34 visible sections, and it broke draw batching for every one
of them on top, a render-thread cost that capture does not even include.

## 17. Map-kind gates: which maps have a sky (`MapSky` / `MapSkyMath`)

**Problem.** Every subsystem in this mod derives its output from the sun, the moon or the sky, and
almost none of them ever asked whether the map they were rendering could *see* any of those. On a
Biomes! Caverns cavern — a sealed map under a rock ceiling — the mod was warming the "sky" at
sunset, shifting its colour temperature along a blackbody curve, tinting it green during a solar
flare, recolouring it crimson for a blood moon, and, worst of the set, lifting §5's night floor
using starlight and **moonlight**. That last one is not a tint: `Patch_NightRadiance` is the only
patch in the mod that writes `SkyTarget.glow`, so an ungated night floor did not merely colour a
cave, it lit one.

Only §13 had ever asked the question, because only §13 was forced to: issue #31 showed that modded
cave environments ship overcast-shaped palettes, so no palette classifier can tell a cloud deck from
a cave ceiling and the map has to be asked directly. The rule it grew (`WeatherDimming.HasSky`) was
correct and already caught all three Biomes! Caverns biomes; it was simply private to weather.

**Approach.** Promote the rule into `MapSky` / `MapSkyMath` and give it a second, distinct
predicate. A fourth question was added later for issue #35 (see "A third kind of map" below). Keeping
them apart is the whole design:

| Question | Asked by | Cavern | Orbit | Open air | Glowforest |
|---|---|---|---|---|---|
| `HasSky` — can weather roll overhead? | §13 only | no | **no** | yes | yes |
| `IsEnclosed` — is there a ceiling? | §2, §5, §8, §10, §10a, §11, §12 | **yes** | **no** | no | no |
| `DrawsShadows` — does this map draw shadows? | §1, §3, §4, §6a, §13, §15 | no | **yes** | yes | yes |
| `SkyBlackedOut` — is the sky opaque *right now*? | §1, §2, §3, §5, §6a, §8, §11, §12, §13a | no | no | no | **yes** |

The first three are all functions of `map.Biome` and cannot change while a map is loaded. The fourth
is not, and that is the whole reason it could not be a clause on any of them.

**Orbit is why there are three and not one.** Orbit is skyless by the weather rule — it offers
exactly one weather — so the obvious implementation, gating everything on `HasSky`, would have
silently stripped every sky effect from orbit while nominally fixing caves. Orbit has no atmosphere
but a completely unobstructed view of the sun and stars, which is the opposite situation from a
cavern. Vanilla already separates them with `BiomeDef.inVacuum` (set by Odyssey's `Space`/`Orbit`,
set by no cave biome), so `IsEnclosed` is `!HasSky && !inVacuum` and costs no def-name list. What
twilight or a colour-temperature shift should mean with no atmosphere to scatter through is a real
question with a different answer from "nothing", and it is deliberately left as separate work rather
than folded into cave compatibility by accident. `map_kind_gates.json` pins orbit at
`map_enclosed 0` / `map_draws_shadows 1` so that folding the predicates back together fails a test.

**Shadows key on `disableShadows`, not on the sky.** That is vanilla's own field, vanilla honours it
itself at `SectionLayer_SunShadows.Visible`, and Biomes! Caverns sets it on all three cavern biomes —
so reading it means our shadow subsystems cannot disagree with vanilla about which maps are
shadowless. `ApiCompatibilityTests.SectionLayerSunShadows_StillHonoursDisableShadows` pins vanilla's
own use of it, because that use is the entire justification for ours. One of the five sites is pure
waste removal rather than correctness: `Patch_ShadowMeshPerimeter` stops building a per-section mesh
that could never be drawn. A second such site — `MapComponent_SunShadowAxis`, which raised a
whole-map dirty flag ~720× per game day — was written and then dropped on rebase, because deleting
§3's across-map shadow tilt reduced that component to an inert tombstone and removed the cost
outright rather than gating it.

**What is deliberately NOT gated.**

- **§7b indoor sky occlusion and §7a pitch-black nights.** A sealed cave rendering dark because it
  has no sky is the correct result and this mod's premise, not a bug. Biomes! Caverns disagrees —
  it transpiles `SectionLayer_LightingOverlay.Regenerate` so its sentinel roof reads as unroofed,
  because it wants ambient-lit caverns — and we do override that, knowingly. Three things make it
  defensible rather than rude: it is purely visual (§7b writes vertex alpha, never `GlowGrid`, so
  no work speed, no plant growth, no solar power changes), the shipped Cinematic preset's
  `MinIndoorBrightness = 0.50` leaves caverns half-lit rather than black, and the slider is the
  documented control for anyone who wants Caverns' look back. Only the Realistic preset (floor 0)
  blacks a cave out, which is exactly what that preset advertises.
- **§9 Purkinje desaturation**, which keys on *measured* glow rather than on the sky and therefore
  already self-corrects to whatever light a cave actually has. Gating it would break it.
- **§14 `Patch_SunGlow`**, which postfixes `CelestialSunGlowPercent(float, int, float)` — no `Map`
  is reachable, so it cannot be gated per-map. It is default-off.

**Two things the live harness corrected, both of which had been asserted confidently first.**

1. **Vanilla's `Undercave` is NOT caught, and nothing sets `disableSkyLighting`.** §13 already
   documented that Undercave carries two weathers because XML inheritance *appends* rather than
   replaces `baseWeatherCommonalities`. What was newly checked here is the other clause, and no def
   — in vanilla or in any of the installed workshop mods — sets `disableSkyLighting` to true at all.
   Vanilla `SkyManager` does read it, so the clause stays as the escape hatch it is, but it is
   currently dead against the real def census and the weather count is doing all the work. Anomaly's
   undercave therefore keeps its sky effects. That is pre-existing §13 behaviour, not a regression
   (`weather_dimming_skyless.json` has always pinned Undercave as dimming), and closing it would be
   a deliberate §13 change rather than part of cave compatibility. `map_kind_gates.json` pins the
   gap so it is recorded rather than rediscovered.
2. **`BMT_RockRoofStable` is `isThickRoof: true`.** So on a cavern map `EavesMath`'s thick-roof veto
   fires and every cell resolves as enclosed rather than as an eave — §7b treats a cavern ceiling
   exactly as it treats a mountain, which is the consistent answer and the reason the cavern reads
   uniformly dark rather than blotchy.

**Biomes! Caverns' partly-open caves are unaffected**, and this was checked rather than assumed.
`BMT_ShallowCave`, `BMT_DesertShallows` and `BMT_GlacialHollows` are Geological Landforms
`BiomeVariantDef`s layered over an ordinary surface tile, carving `RoofRockThick` regions into it
instead of stamping the cavern sentinel roof. Geological Landforms patches `Map.Biomes` (plural) and
**not** `Map.Biome`, so `map.Biome` stays the surface biome, every gate stays open, and the roofed
rock is handled per-cell by §7b as real thick roof. `map_kind_gates.json` reproduces that shape and
pins `map_enclosed 0`.

### A third kind of map: a sky with nothing visible through it (issue #35)

**Problem.** The two gates above are both properties of a `BiomeDef`, and there is a third kind of map
neither covers: one with a sky, no ceiling and shadows enabled, but *nothing visible overhead*.
Odyssey's `Glowforest` is the headline case and says so in its own description — "geysers spew out
massive sulfur clouds that cast the region into permanent darkness" — implemented as a permanent
map-wide `DarkenedSkies` condition in `biomeMapConditions`. It is otherwise an ordinary surface tile
offering **eleven** weathers and setting none of `disableSkyLighting` / `disableShadows` / `inVacuum`,
so both gates stayed open and every sun- and moon-derived effect ran on it.

It is also not biome-specific and not static: Odyssey's `AncientSmokeVent` causes the same
`DarkenedSkies` on **any** map that has one, cycling roughly 3 days on / 4 days off. Royalty's
`SunBlocker` machine is a third source. All four blackout sources in the game (those three plus the
eclipse) are `GameCondition_NoSunlight`.

**Approach.** A fourth predicate, `MapSky.SkyBlackedOut(map)`, keyed on the condition **class** rather
than a def-name list — so a modded blackout condition is caught for free, exactly as `IsEnclosed`
catches every modded cave biome through vanilla biome data. It walks the map's `GameConditionManager`
chain and then the world's, filtering on `CanApplyOnMap` and *nothing else*, because that is precisely
the filter `SkyManager.CurrentSkyTarget` applies when composing a condition's `SkyTarget` — so our gate
opens and closes on the same frames vanilla's own darkening does. `HiddenByOtherCondition` is
deliberately not consulted: it reports `silencedByConditions` and governs the UI label, while
`CurrentSkyTarget` darkens the sky regardless.

**The eclipse is carved out**, and that is the one way to get this wrong that nothing downstream could
catch: `Eclipse` is the same class, §10/§10a exist to reshape it, and an eclipse sky and a sulfur sky
read near-identically in every effect probe. It is excluded by def, the mirror image of
`Patch_EclipseDarkening` excluding `SunBlocker` by def. It is also a genuinely different fact about the
world — an eclipse covers the *sun* while leaving the sky transparent, which is why stars come out
during a total one and nothing comes out under a sulfur overcast.

**Vanilla looks like it already handles this, and does not.** `GameCondition_NoSunlight`'s
`EclipseSkyColors` carries `Color.white` as its shadow colour, which reads as "suppress the shadow" —
but `SkyManager` composes conditions with `SkyColorSet.LerpDarken`, i.e. `A.shadow.Min(B.shadow)` per
channel, and white is the per-channel **maximum**. That white has always been a no-op, and
`GenCelestial.CurShadowStrength` is a pure function of `CurCelestialSunGlow` with no condition term at
all, so a blacked-out map keeps its full-strength cast shadows in vanilla too. Measured live on
Glowforest with `DarkenedSkies` at a low sun: `MatBases.SunShadow.color.r` read **0.778** — a 22%
ground darkening with crisp sun-angled bands — under a sky the mod had just driven to zero glow.
`Patch_ShadowStrength` now writes 0 there, which whitens the material and removes the band
(`sky_blackout_gate.json`).

**What that per-frame write buys for free: no mesh invalidation anywhere.** The obvious worry with a
*dynamic* gate is §16's baked section meshes — a condition starting or ending would have to dirty them
the way `IndoorOcclusionRedraw` does. It does not, because every map-wide magnitude in this mod is a
per-frame material or shader-global write and only the per-cell *shape* lives in a mesh, and no shape
depends on the sun: §15b's eave shade reads the finished `MatBases.SunShadow.color` (so it whitens on
the same frame, with its mesh untouched), §9's wash strength is re-derived from `CurSkyGlow` every
`SkyManagerUpdate`, and §7b's occlusion never consults sun or moon at all. Gating
`Patch_ShadowMeshPerimeter` or `SectionLayer_EaveShade.Visible` *would* have needed invalidation, and
both are deliberately left alone: their output is already invisible once the material is white, so the
only thing a gate could buy there is waste-removal, paid for with a dirty-flag storm on a
several-times-a-week condition.

**The colour half is real but small, and the pin says so.** §2's twilight target and §8's blackbody
tint both raise red and *lower* blue, and `LerpDarken`'s per-channel min preserves a lowered channel —
so a blacked-out dusk kept most of our warmth and only had its red clipped to the condition's own
0.482. Measured, the gate moves the composed sky's red-minus-blue from +0.0139 to -0.0096 at dusk:
visible as "neutral rather than warm-by-a-hair", not as the dark orange a first reading of the
composition suggests. §5's night floor was already being flattened by vanilla before the gate, because
`glow` is the one channel `LerpDarken` genuinely collapses (`Lerp(A.glow, Min(A.glow, 0), t)`); gating
it stops the mod asserting moonlight it cannot see and closes the partial-lerp window during a sun
blocker's 200-tick fade-in.

**A blacked-out map is permanent night, and that is vanilla's doing rather than ours.** Once the
condition actually applies, `LerpDarken` drives glow to `Min(A.glow, 0)` and the sky colour to the
per-channel minimum of the weather palette and `EclipseSkyColors`, both hour-independent — so the map
has no day left in it. Measured across a full day on a blacked-out Glowforest the composed frame sits
at mean RGB 22.6/24.8/23.9 at noon, 17:00 and 23:00 alike, against an open-sky midnight of
22.3/24.8/23.4. Those numbers are identical before and after this change, so the gate neither creates
nor removes the property; what it does is stop §5's night floor from being the one thing that could
have reintroduced a cycle (vanilla was already flattening it, so this is belt-and-braces) and remove
the sun shadows that were the only remaining evidence of a sun. `sky_blackout_gate.json` pins noon
against midnight directly, because a diurnal term creeping back in is the regression a screenshot
would show last. The single exception is dusk, where Clear's palette carries blue 0.423 — *below* the
condition's own 0.682 — so the per-channel min keeps the weather's lower blue and the frame reads
~6/255 poorer in blue than the rest of the day. Vanilla's composition, small, and left alone.

**A harness trap this work fell into and the scenarios now document.** `GameConditionUtility.LerpInOutValue`
returns 0 for a condition whose `TicksPassed` is negative *or* whose `TicksLeft` is, so a registered
condition can contribute nothing to the sky while still being present. Both routes were hit:
`StartCondition`'s `durationHours: 0` documents itself as permanent but reaches
`GameConditionMaker.MakeCondition(def, -1)`, and `GameCondition.Duration`'s setter sets
`permanent = false` — so `TicksLeft` is negative rather than the condition being permanent; and a
`SetTime` step that rewinds the clock behind `startTick` makes `TicksPassed` negative. The first draft
of `sky_blackout_gate.json` hit the former, which is worth recording because of *how* it hid: our gate
keys on the condition's presence and so fired correctly regardless, every probe passed, and the frames
behind them were dark because of the Eclipse the same scenario started for the carve-out check. One
condition per scenario (hence `sky_blackout_eclipse_carveout.json`) plus `agedHours` back-dating is
what makes either file's darkness attributable to the thing it names.

**Binary rather than a strength factor**, deliberately. Both real cases are step functions —
`DarkenedSkies` is `GameCondition_NoSunlight_Instant` (`TransitionTicks` 0) and Glowforest's is
permanent — so the only thing a float would buy is Royalty's `SunBlocker` fading in over 200 ticks
(3.3 s), against threading a blend factor through seven patches. The hook if it ever matters is
`SkyTargetLerpFactor`.

**§10a still fires natural eclipses on a blacked-out map**, unlike on an enclosed one. A transit is a
celestial fact and §10a fires on geometry; making eclipse cadence hostage to a smoke vent's 3-on/4-off
cycle would silently thin the ~one-per-few-years rate `natural_eclipse` pins, for an event whose
gameplay consequences (solar power) are already gone under the blackout anyway. §11's aurora keeps its
own separate `GameConditionManager.IsAlwaysDarkOutside` guard too — vanilla's version of this question,
narrower on both axes (permanent only, eclipse-blind) — because its justification is different: it
mirrors what `GameCondition_Aurora` does to itself.

**Conflict risk.** `MapSky` reads only vanilla `BiomeDef` fields (`disableSkyLighting`,
`disableShadows`, `inVacuum`, `baseWeatherCommonalities`), vanilla `GameCondition` state and
`GameConditionDefOf.Eclipse`, and takes no reference to any third-party assembly, so there is nothing
here for another mod to collide with — a mod that wants its map treated as enclosed gets there by
declaring biome data, exactly as Biomes! Caverns already does, and one that wants a blackout gets there
by subclassing `GameCondition_NoSunlight`, which is what a blackout already is.
Not cached, deliberately, even at nine callers: every call is per-map-per-frame at worst, and a
BiomeDef-keyed cache would have to be invalidated against the harness's own `SetBiome` step, which
mutates `map.Biome` at runtime so scenarios can sweep biomes inside one run. `SkyBlackedOut` is
uncached for a stronger reason: a colony carries a handful of conditions against the dozen weather
entries `IsEnclosed` already walks at the same call sites, and it is the one predicate here that
genuinely changes mid-session, so a cache would need the `RegisterCondition` / `OnConditionEnd` hooks
the static gates do not — cost and risk on the same side. Vanilla makes the opposite trade with
`GameConditionManager.cachedAlwaysDark` and pays exactly those two hooks for it.

## 18. Vacuum maps (`Vacuum.cs`) — the shared `inVacuum` gate

Odyssey adds space maps (orbital platforms, gravships in transit). Every subsystem above models
light travelling *through atmosphere*, and vanilla runs the full ground lighting cycle on those maps
regardless: `SkyManagerUpdate → CurrentSkyTarget → WeatherWorker.CurSkyTarget` opens with
`GenCelestial.CurCelestialSunGlow(map)`, and `Space`/`Orbit` set neither `disableSkyLighting` nor
`disableShadows`. So our patches all fire normally 200 km up, and several of them are wrong there.

### Detection

`BiomeDef.inVacuum` is the discriminator, and it is a field on **base** `RimWorld.BiomeDef` — all
DLC code ships in the base assembly, verified by decompiling 1.6 `Assembly-CSharp.dll` and pinned by
`ApiCompatibilityTests.BiomeDef_HasInVacuum`. So `map.Biome.inVacuum` compiles and evaluates with
Odyssey uninstalled, reading `false` on every vanilla biome. **No `ModsConfig.OdysseyActive` gate and
no soft-reference plumbing** — that would add a second branch that can only ever agree with this one,
and a second thing to keep in sync.

This is deliberately a per-*map* question. Per-cell questions (a pressurised gravship hull on a
vacuum map) have `Verse.VacuumUtility.GetVacuum(cell, map)` / `IsRoomAirtight(room)`; nothing in
§18a needs them, since sky colour is a whole-map property. §7b's indoor occlusion is the subsystem
most likely to want the per-cell version later.

### The convention (`Source/Vacuum.cs`)

One gate, threaded the same way everywhere, because several subsystems land on it and each inventing
its own plumbing would leave no single place to check whether a map is airless:

1. The **adapter** — a `Patch_*` file, or a thin helper that already takes a `Map` — calls
   `Vacuum.InVacuumForMap(map)` exactly once and passes the resulting `bool` down. That function is
   a one-line `map.Biome.inVacuum` and is the only place in the mod that reads the field.
2. The **pure-math function** takes `bool inVacuum` as its **last parameter, required and never
   defaulted**, and early-returns the vacuum value at the top before any atmospheric math runs. A
   defaulted parameter would let a new call site silently opt out of a gate whose whole value is
   that you cannot forget it. The branch lives with the math it suppresses, not in the adapter, so
   the shipped behaviour and the unit-pinned behaviour are literally the same code — an adapter-side
   early-out would leave the pure function still able to return a nonzero atmospheric value that
   nothing tests and nothing renders.
3. The **offline test** pins the vacuum value *and* its sea-level counterpart in the same
   `[TestCase]` sweep (`Tests/CelestialLighting.Tests/VacuumSuppressionTests.cs`). Asserting only
   "vacuum == 0" passes just as happily when the sea-level effect has itself regressed to zero,
   hiding a broken subsystem behind a green vacuum test; pinning the pair makes either regression
   show up as a divergence rather than as a single number quietly agreeing with a stale expectation.

Live probes read the same gate as their patch (`SkyColorTemperatureProbe`,
`AuroraConditions.CurrentSkyTintStrength`) — the same discipline `SolarPosition.cs` enforces between
the shadow patches. A probe reporting a tint that nothing renders would be worse than no probe.

### 18a. The three effects that collapse

Grouped because none of them needs new math, only the gate.

| Subsystem | Vacuum behaviour | Why |
|---|---|---|
| Twilight colour (§2, `Formulas.TwilightFactor` / `TwilightWarmthFactor`) | **Zero**, at every elevation and latitude | Twilight *is* scattering — sunlight lighting air the ground can no longer see the sun through. No air, no twilight. Not a shortened ramp: the contribution goes to 0. The below-horizon civil-twilight persistence term goes too; it is if anything the more atmospheric of the two pieces, since it exists precisely because scattered light keeps lighting the sky after the sun is geometrically down. |
| Sky colour temperature (§8, `SkyColorTemperature`) | `ColorTemperatureKelvin` **pins flat to `ZenithKelvin`**; `TintStrength` goes to **0** | Warm-at-low-sun is Rayleigh reddening through a long air path; the sun's own emitted spectrum does not change as it descends. |
| Aurora tint (§11, `AuroraMath`) | **Off** — both sky and overlay strengths | The 630 nm emission sheet is ~630 km up; an orbital platform sits at 200 km, so you look *down* on a localised curtain rather than up through a sky-filling one. A full-screen colour blend is the wrong *shape*, not merely too strong, which is why it is a hard zero and not a scale factor. |

The colour-temperature row needs both halves, and this is the one non-obvious bit. Pinning the
Kelvin alone does **not** flatten the effect: the Helland fit puts 5772 K at roughly
`(1.00, 0.95, 0.90)`, not pure white, so an elevation-dependent blend toward it would keep creeping
amber into the sky as the sun dropped — the exact artefact §18a exists to remove. Zeroing
`TintStrength` is what makes it flat. Pinning the Kelvin is what keeps every *other* consumer of the
curve honest: the `sky_color_temperature` probe, and the limb-refraction work below, which needs an
unreddened reference to redden away from.

Both twilight paths take the gate, including the legacy glow-keyed-only one behind
`CelestialLightingFeatures.CivilTwilightPersistence`, so turning that feature off cannot smuggle
ground twilight back onto a space map.

**Accepted consequence: §18a alone leaves a hard colour step at the orbital terminator.** That is
correct as far as it goes — the ground twilight it removes was never physical there — but it is not
the finished look. The deep-red limb refraction that physically replaces it (sunlight bent through
the planet's atmospheric limb, the same physics that makes an eclipsed moon copper) is its own piece
of work and must land after this, not instead of it.

### What is *not* gated, and why

- **Seasonal shadow lean (§1/§3) keeps its referent.** RimWorld's orbits are stationary:
  `PlanetLayer.LongLatOf(tileID)` derives lat/long from a static `GetTileCenter`, and nothing gives
  an orbit-layer tile a period or a phase. A space map sits permanently above one lat/long and gets
  the same 24-hour cycle, seasonal sun path, and latitude behaviour as the surface tile below it. So
  there is no 16-sunrises-a-day problem to model, and the lean is exactly the surface tile's.
- **Weather dimming (§13) is already structurally zero.** `Space` rolls only `Orbit`, whose palette
  §13's classifier already rates clear-family `(1,1,1)`; separately §13's `HasSky` rule ("fewer than
  two nonzero weather commonalities ⇒ no sky") independently catches `Space`, which declares exactly
  one. Two independent reasons for the right answer — see the palette table in `WeatherDimmingMath.cs`.
- **Night light budget, shadow contrast, and eclipse response** each need real math rather than
  suppression, and are tracked separately.

### The elevation half — scoped out

There is no continuous per-map altitude in vanilla to key a scale-height model to.
`PlanetLayerSettings.extraCameraAltitude` is a *camera* parameter and `PlanetLayerDef.elevationString`
is a display string, so what is actually available is a two-state model (surface layer vs. orbit
layer), not a smooth "thinner air with height" ramp. A continuous model would be invented and
anchored to something arbitrary, which cuts against how the rest of the mod is pinned.

### Verification

Offline: `Tests/CelestialLighting.Tests/VacuumSuppressionTests.cs` pins every effect's vacuum value
against its sea-level counterpart across a sun-elevation sweep.

Live: `Tests/Scenarios/vacuum_suppression.json`, which runs the same latitude (45), day (40) and
hours on a planet surface and then on a real Odyssey orbital platform in one load, via the harness's
`LandInOrbit` step. The ground half pins the effects alive and the orbit half pins them collapsed, so
the scenario fails if the gate ever stops firing *or* if it starts firing on the ground. Measured:

| Hour | | twilight_warmth | sky_color_temperature | aurora_tint |
|---|---|---|---|---|
| 19 | ground | 0.3520 | 2851.66 K | 0.0388 |
| 19 | orbit | **0** | **5772 K** | **0** |
| 21 | ground | 0.1531 | 2000 K | 0.1800 |
| 21 | orbit | **0** | **5772 K** | **0** |
| 22 | ground | 0 | 2000 K | 0.1800 |
| 22 | orbit | **0** | **5772 K** | **0** |

On the pre-gate build the orbital column came back **bit-identical to the ground column** at every
hour, which is the sharpest statement of what §18a fixes: an orbital map was not merely resembling
the ground's atmospheric colours, it was computing exactly them.

Two things worth recording about the pins. First, they are **calibrated from a live run, not
predicted** — the offline sweep's helper derives sun glow from our own simulator's elevation, but the
live pairing is looser than that, because `aurora_tint` keys on vanilla's `GenCelestial
.CurCelestialSunGlow` while `sky_color_temperature` keys on our `SolarPosition` elevation. Those are
two different sun models and they do not correspond exactly; predicting one from the other is off by
a few degrees. The unit tests are unaffected (they pin pure functions at given inputs), but a live
pin has to be measured.

Second, latitude pinning matters more here than usual: with stationary orbits the platform's lat/long
fully determines its day length and sun path, and the orbit layer is a subdivided icosphere whose
tiles are a couple of degrees wide. `LandInOrbit` therefore pins `WorldGrid.LongLatOf` to the
requested latitude rather than accepting whatever the subdivision produced.

§18d is a *temporal* effect, so per the project workflow its live validation wants a video of a
full orbital sunset plus `limb_*` probe readings at fixed sun elevations through the band, rather
than a still A/B.

## 18b. Vacuum night light budget (`VacuumRadianceMath` / `NightRadiance`)

**Problem.** §7 makes night the sum of three dim sources — starlight, airglow, moonlight — and every
one of them is a statement about standing at the bottom of an atmosphere with a moon overhead. On an
Odyssey vacuum map (`BiomeDef.inVacuum`, the §18 gate) all three are wrong, and — the part that is
easy to get backwards — they are not all wrong in the same direction.

**Approach.** Three substitutions, in `Source/VacuumRadianceMath.cs`:

| term | sea level | vacuum | why |
|---|---|---|---|
| airglow | 0.02 | **0** | the emission layer is at ~90 km; a 200 km platform is *above* it |
| starlight | 0.02 | **0.031** | no atmosphere, no extinction — the term goes **up**, not down |
| moonlight | phase × altitude | unchanged | still up there, still lit (see below) |
| planetshine | — | ~0.0005 | new term, and derived to be negligible (see below) |

**Starlight is the sign trap.** §7's 0.02 is a sea-level floor, so it is an *already-extinguished*
number and the vacuum value is that floor **divided** by the transmittance, not multiplied. The
transmittance is an integral rather than a `sec(z)`: starlight arrives from the whole hemisphere at
once, each direction extinguished by its own airmass and contributing by its own cosine, so the
figure that matters is the projection-weighted mean

```
T = 2 ∫₀¹ 10^(−0.4·k/u) · u du,      u = cos(zenith angle)
```

which at `k = 0.28` mag/airmass (sea-level clear, visual band) is **0.641** — well below the
zenith-only 0.773, because the heavily-extinguished low sky is a lot of sky. So the vacuum gain is
1.56× and starlight lands at 0.031.

The claim being made is about the *ratio*, not the absolute value. §7's 0.02 is explicitly a
look-calibrated floor, not a photometric one, so asserting a photometric number in vacuum would be a
category error; applying the ratio preserves the calibration while encoding the physics of the
change. Nothing here is sensitive to the coefficient: across the whole published 0.20–0.30 range the
gain runs 1.38–1.61 and every claim below survives, which `VacuumRadianceMathTests` pins directly.

**Planetshine, and the three open design questions.**

*Does planetshine get a phase model, or is a constant floor honest?* **A constant, and the constant
is derived to be ~zero.** The reasoning in the issue was that a sun–platform–planet phase term
degenerates at a fixed lat/long into "the inverse of your own daylight", which the sun term already
encodes. That is right, but it undersells the result, because the geometry says something sharper.
At 200 km the platform sees a cap of ground `acos(R/(R+h)) = 14.17°` of surface arc wide, so it can
see terrain up to 14.17° of solar depression *ahead* of its own — genuinely still lit by a sun the
platform has lost. Integrating that cap (exact spherical trig for each ground point's solar
elevation, Lambertian reflection at albedo 0.30, weighted by subtended solid angle and view cosine)
gives planetshine as a real function of the platform's own sun elevation. Evaluated at the top of
the full-night band, −18°, where it is largest, it comes to **0.00085 lux** — about 1/300th of a
full moon, and in glow units 40× under §13a's perceptibility threshold. Below −32.2° it is
identically **zero**, because by then even the far limb of the visible cap is past astronomical
twilight.

So planetshine is not a term that needs a phase model; it is a term that dies of its own accord
exactly where the night floor lives. The shipped floor uses the −18° value as a constant, which is
the supremum over the band and therefore errs toward *not* claiming orbit is darker than it is. The
function is still there and still exact, because computing the answer is what makes "planetshine is
negligible" a result rather than an assertion — and #32 will want the day-side branch of the same
integral.

One thing the integral deliberately excludes: `AmbientSkyLux` clamps to a 0.001 lux night floor
which *is* starlight and airglow, and bouncing that off the ground and counting it as planetshine
would double-count the very starlight term §18b just finished raising. Only the solar part of the
ground's brightness counts. That subtraction is precisely what drives planetshine to exactly zero at
deep night.

*Does the moon survive alongside planetshine, or get replaced?* **It survives, unchanged.**
Planetshine outranks the moon over the day side by four orders of magnitude, but the night floor is
only ever consulted at orbital night, and there planetshine is ~zero and the moon is the *only*
reflected source left. Replacing it would flatten every orbital night to one value and throw away
information the model already has. It is passed through unrescaled, deliberately: `MaxMoonlightGlow`
is a look-calibrated amplitude (§7) rather than a photometric quantity, and §7a's overlay ramp is
anchored on it (`floors + full moon at zenith == vanilla brightness`), so applying an extinction
gain here would move an invariant for no visible gain. The genuinely photometric half of the moon —
`IlluminanceMath.FullMoonZenithLux`, which *is* a sea-level number — belongs to #31, where the moon
appears as a shadow caster rather than as a floor.

*Settings knob or derived value?* **Derived, and nothing new is exposed.** There is no vacuum
slider. The only place a setting enters is the lux→glow conversion, which is anchored on the moon
(`ReflectedGlow(lux, maxMoonlightGlow)`) so that turning reflected night light down turns planetshine
down with it. Anchoring on the moon rather than inventing a lux→glow scale matters: §7's two night
anchors (0.001 lux → 0.04 glow, 0.267 lux → 0.15 glow) imply scales 70× apart, because glow is
deliberately compressive. Glow units are **summed** in §7's model and a compressive mapping is not
additive, so a log fit would double-count the moment a second source appeared. The moon is a linear
ratio and the right comparison class — planetshine and moonlight are the same physical thing,
sunlight bounced off a nearby rock.

**The design claim, and the test that owns it.** Because RimWorld's orbits are stationary
(`PlanetLayer.LongLatOf` derives lat/long from a static tile centre; nothing gives an orbit tile a
period — see the epic), a platform in the planet's shadow hangs directly over the planet's *own*
night side. Planetshine is therefore at its minimum exactly when fill light would matter, and

> **orbital night is the darkest state this mod can produce — darker than any surface night,
> including a new moon.**

0.0317 against 0.0400 at shipped defaults. `VacuumNightFloor_IsDarkerThanEverySurfaceNight` was
written before any of the physics under it and is the assertion that must survive a retune; the
individual anchors are not. Note it is a claim about **floors**: a vacuum night under a full moon is
legitimately brighter than a surface new-moon night, because the moon is unaffected by any of this.

No §7a change was needed for the screen to follow: the overlay ramp reads the actual glow, so a
moonless orbital night keeps 0.0317/0.19 ≈ 17% of vanilla overlay brightness against the surface's
21%. Darker on screen too, and it falls out.

### The shared night floor (`NightRadiance.FloorGlowFor`)

§18b is also where the night floor stopped belonging to §7 alone. Three subsystems need the same
number:

- **§7** blends the night sky toward it (`Patch_NightRadiance`)
- **#31** vacuum shadow contrast — what a cast shadow bottoms out at once the skylight fill is gone
- **#33** vacuum eclipse response — the umbral minimum totality falls to

```csharp
// pure core
NightRadianceMath.NightFloorGlow(
    float starlightGlow, float airglowGlow, float moonlightGlow,
    float maxMoonlightGlow, bool inVacuum)

// live-state adapter
NightRadiance.FloorGlowFor(Map map)
```

One value reached from three directions, the same discipline `SolarPosition` enforces for sun
elevation and `WeatherDimming` for cloud cover — and §9's `WeatherDimming.DimmingFor(map)` is the
exact shape being copied. A **shared read**, not one patch stashing a value for another to pick up:
a read has no patch ordering to get wrong, no staleness across frames, and no silent dependency on
which Harmony patch ran first.

Two deliberate non-behaviours. It reports the *floor*, not the current sky — no sun-elevation ramp
(`ApplyNightFloor` owns that) and no weather multiply (§13 owns that, on the colour channel). And it
is **not** gated on `CelestialLightingFeatures.NightRadiance`: that flag restores vanilla's flat
night glow, a value this mod does not own and cannot report, so making the shared floor jump whenever
an unrelated toggle flipped would be worse than useless to #31 and #33. Each consumer gates its own
effect.

The `inVacuum` discriminator is the last parameter and the vacuum branch returns before any
atmospheric math runs, per the convention `Vacuum.cs` sets out for the whole §18 epic. Note the
parameter asymmetry: `airglowGlow` is accepted and then ignored in vacuum, `maxMoonlightGlow` is used
only in vacuum. Both are passed unconditionally so a call site never has to know which atmosphere it
is in — which is the entire point of the discriminator being an argument.

**Conflict risk.** None new. `VacuumRadianceMath` is pure and referenced only by `NightRadianceMath`;
`NightRadiance` adds no patch of its own and `Patch_NightRadiance` keeps the same single postfix on
`WeatherWorker.CurSkyTarget` it always had. No `ModsConfig.OdysseyActive` gate and no soft reference:
`inVacuum` is a field on base `RimWorld.BiomeDef`, so this compiles and reads `false` on every vanilla
biome with Odyssey uninstalled.

**Verification.** Offline in `VacuumRadianceMathTests` (22 cases). Live verification is blocked on
`Jeffrharr/RimWorldTestHarness#17` — scenarios cannot currently reach an orbital map, because
`SetTile`/`SetBiome` target planet-surface tiles and `OrbitLayer.CanSelectLayer` refuses the layer
unless a world object already exists on it. Nothing here has been validated in a running game.

## 18c. Vacuum shadow contrast (`ShadowFillMath`)

**Problem.** A cast shadow is never black, and the reason is not geometry. Stand in one at noon and
you are still lit — by the whole dome of scattered sky above you. That fill is the entire content of
vanilla's `SkyTarget.colors.shadow`, which `SkyManager` uses as the darkest colour any shadow can
render (`Color.Lerp(white, colors.shadow, CurShadowStrength)`). Clear's daylight value is a faintly
blue grey, `(0.718, 0.745, 0.757)`: a shadow keeps 74% of the lit ground's brightness, and the blue
is Rayleigh scattering turning up in a colour channel.

Take the air away and that number has no cause left. On an orbital platform §13a's substitution is
modelling a dome that is not there, so a vacuum shadow renders at a 26% darkening when the physics
says it should be nearly black.

**Approach — a split, and the split is the whole subsystem.**

| | |
|---|---|
| **KEPT, untouched** | The geometric penumbra (`PenumbraMath`). The sun subtends ~0.53° whether or not there is air in the way, so the widening of the penumbra as shadows lengthen is *identical* in vacuum. |
| **DROPPED** | The umbra floor (skylight fill) and the sky-palette tint. Both are the atmosphere's contribution and both leave with it. |

`ShadowFillMath.DaytimeUmbraFill(skyFillR, G, B, nightFloorGlow, litGlow, inVacuum)` is the single
gated entry point, shaped to §18's convention (`Source/Vacuum.cs`): `inVacuum` last, required, vacuum
value returned before any atmospheric term is read. Note what that shape buys beyond consistency —
the three `skyFill` arguments are simply **not consulted** in the vacuum arm, so "drop the sky tint"
is structural rather than a comment promising it.

It ships inside `Patch_WeatherShadowColor` rather than as a fourth patch. That file is *the* daytime
writer of `colors.shadow`, and what §18c changes is not when it writes but which fill it writes; two
postfixes on `WeatherWorker.CurSkyTarget` both writing the field would be decided by whichever
Harmony happened to order last. The full ownership table, all three arms splitting on shared
discriminators (`SolarPosition` + `Formulas.AtmosphericRefractionDegrees`, and `Vacuum.InVacuumForMap`):

| regime | owner |
|---|---|
| sun down | §6a `Patch_MoonShadowColor` — the moon is the caster |
| sun up, has atmosphere | §13a — Clear's skylight fill |
| sun up, in vacuum | §18c — §18b's night floor |

### What actually fills a vacuum umbra — the question this had to answer rather than assume

The issue's premise was that the vacuum shadow colour "comes from the reflected-planet term". Working
it out changed the answer, and the intermediate numbers are worth recording because the intuitive
result is wrong in *both* directions.

**Planetshine is not small in daylight.** §18b evaluates `PlanetshineLux` only at astronomical
twilight, where it is 0.00085 lux and rounds away. Above the horizon it is enormous — 33,000 lux
under a zenith sun, 27.5% of the ground illuminance (essentially `albedo × discFill = 0.30 × 0.94`).
Had it reached the deck, the vacuum umbra would keep ~0.27 and orbital shadows would be only modestly
harsher than sea level's 0.74, not the near-black this subsystem renders.

**It does not reach the deck, and the reason is the platform.** RimWorld's orbits are stationary
(epic #8), so a platform hangs over a fixed lat/long and its deck faces that tile's *zenith* — exactly
the direction away from the planet; the sun rises and sets across that deck the way it does on the
ground below. The planet is therefore under the **floor**. Every cell on the deck has the platform's
own structure between it and the planet, so planetshine lands on the underside and nowhere else.

So a shadow on the deck is filled by what an upward-facing surface in vacuum can see: the star field
(unextinguished — §18b's term that goes *up*) and the moon. Which is precisely §18b's published night
floor, so §18c **consumes** `NightRadiance.FloorGlowFor(map)` and derives nothing. "How dark can the
sky over this map get" and "what does a shadow here bottom out at" are one physical question asked
from two directions, and the shared read is what makes them provably the same number rather than two
patches computing the same sum and hoping.

*Recorded for whoever evaluates `PlanetshineLux` above the horizon next:* the occlusion argument
applies to §18b's own planetshine term too. At 0.00048 glow it is 1.5% of the vacuum night floor and
moves no digit anyone can see, so it is left exactly as §18b defined it — but a future subsystem that
reaches for that function in daylight (#32's limb flash is the obvious candidate) must apply the
occlusion argument first or it will be off by four orders of magnitude.

**And so the vacuum umbra is neutral grey** — not because grey is safe, but because both surviving
sources are: an integrated star field is very nearly white and moonlight is neutral. There is no sky
palette left to take a hue from, and the reflected-planet term that would have supplied one is the
term the deck cannot see. §6a already writes a neutral grey at night for the adjacent reason (§9's
low-light desaturation owns the night's colour cast), so this agrees with the mod's existing answer
instead of inventing a second one.

### The photometric half of the moon term (`VacuumRadianceMath.MoonlightGlow`)

§18b kept the moon as a source in vacuum and left its photometry open. §18c needs it closed, because a
moonlit vacuum umbra is the one case where a vacuum shadow is meaningfully non-black, i.e. the one
case where the moon term *is* the shadow's fill.

The correction is a calibration error, not a modelling change. §7's glow scale is anchored on
`IlluminanceMath.FullMoonZenithLux` = 0.267 lux, a **measured sea-level** figure — the moon's light
has already crossed the atmosphere by the time anyone reads it off a light meter. Above the atmosphere
the same full moon delivers `0.267 / 0.773 = 0.346` lux, so through the same linear scale it is worth
`1 / ZenithTransmittance = 1.294×` the glow. Nothing about the moon changed; we stopped charging it
for air that is not there.

Two things that look like they need the same fix and do not:

- **Not the full airmass law.** §7's `MoonAltitudeFactor` is a pure `sin(elev)` *projection* and models
  no extinction at all, so a low moon is already treated as unextinguished at sea level. The only
  extinction baked into the sea-level model is the one inside the zenith calibration constant, so the
  zenith factor is exactly the error there is to remove. An airmass-dependent divisor would be
  correcting a term §7 never got wrong and would make a low vacuum moon implausibly bright.
- **Not `ReflectedGlow`.** It uses the same sea-level constant, but as a *lux-per-glow scale*, and that
  scale is a property of the renderer rather than of the sky: the vacuum full moon is both 1.294× the
  lux and 1.294× the glow, so the ratio is identical in either regime.

The pair that falls out is the headline, and §18c inherits it directly:

| night floor | sea level | vacuum |
|---|---|---|
| new moon | 0.0400 | **0.0317** (airglow gone) |
| full moon at zenith | 0.1900 | **0.2258** (nothing dimming it) |
| dynamic range | 4.75× | **7.1×** |

Orbit has strictly more range between its darkest and brightest nights than the ground does. An
orbital umbra is near-black on a new moon and visibly grey under a full one — vacuum shadows are
*harder*, not uniformly black.

### Why the umbra keep is a divide

`colors.shadow` is a **multiply** on ground the sky has already dimmed. Writing the floor straight in
would not mean "the umbra bottoms out at the night floor"; it would mean "the umbra is the night floor
*times* the current daylight", which at a low sun lands several times darker than an actual vacuum
night — a shadow darker than the darkness. `VacuumUmbraKeep = floor / litGlow` converts the absolute
floor into the relative multiply the renderer wants, with no tuned constant: at full daylight the
umbra keeps exactly the floor, and as the lit ground dims toward it the ratio climbs toward 1 on its
own.

### What is softened, stated plainly

One thing, and it is a bound rather than a taste call: `VacuumUmbraKeep` clamps at 1. With the fill at
or above the direct beam there is no contrast left to draw, and a keep above 1 would render a shadow
*brighter* than the ground beside it. Nothing else is softened. The resulting lit/shadow contrast —
0.032 against sea level's 0.740, a 97% darkening against a 26% one — is an order of magnitude harsher
than anything else the mod renders, and is deliberately left that way.

Nothing scattering-derived is layered on top of the geometric penumbra, and nothing needed removing:
§13's `ShadowContrastFactor` is the only scattering term in the shadow path and it is already
structurally zero on `Space` maps (§13's `HasSky` rule and its clear-family palette rating both
independently return 0 — see §18 and §13). `PenumbraContrastFactor` is *not* scattering-derived: it is
the 2D approximation of the geometric penumbra itself, and it stays.

### Ordering audit: does anything downstream assume a non-black umbra?

Required before landing, because §15b's eave shade and §9's night wash both composite shadow colour.
Findings, in full:

- **§15b eave shade — one documented claim was wrong, no code was.** `EaveShadeOverlay` derives its
  alpha from the *composed* `MatBases.SunShadow.color`, so it follows §18c automatically with no
  vacuum branch: a vacuum eave lands at `0.605 × 0.032 ≈ 0.019` of open sunlit ground, or exactly 0
  with the atmospheric floors off. That is the right answer for the same physical reason the cast band
  beside it goes near-black — with no dome overhead, being under a roof and being in a shadow are the
  same amount of "no direct sun, and nothing filling in". What did need correcting is
  `EaveShadeMath`'s header, which stated "it cannot go pitch black" as a universal guarantee when it
  was an *atmospheric* one (bounded below by vanilla's darkest palette entry, Clear's 0.740). The
  bound was documented, never enforced in code. Header amended; the sea-level floor (~0.45) and the
  vacuum floor (~0.019) are now pinned as a pair in `EaveShadeMathTests`.
- **§9 night desaturation — no interaction, in either regime.** `SectionLayer_NightDesaturation` reads
  local *glow*, never `colors.shadow`, and its wash is scaled by `PurkinjeMath`'s rod-vision factor,
  which is 0 in daylight. §18c is a daytime-only writer, so the two never overlap. At night on a vacuum
  map §18c does not write `colors.shadow` at all — §6a owns it.
- **Sibling branches, checked at the commit level.** `eave-shadow-gap` has no unique commits (already
  merged). `shadow-alpha` is merged in content and touches shadow *geometry* (mesh extrusion, the
  vertex-alpha bake), never shadow colour. `layer-fanout` is comments plus a timing probe.
  `nightdesat-cost` memoises §9's glow reads. None assumes anything about the umbra's value.

**Conflict risk.** None new. `ShadowFillMath` is pure and referenced only by
`Patch_WeatherShadowColor`, which keeps the single postfix on `WeatherWorker.CurSkyTarget` it always
had — no new vanilla member is touched, so no `ApiCompatibilityTests` addition beyond §18's existing
`BiomeDef_HasInVacuum` pin. Any other mod writing `colors.shadow` composes exactly as it did before,
since the number written changed but the writer did not.

**Verification.** Offline in `ShadowFillMathTests` (30 cases), written as vacuum/sea-level pairs per
§18's convention so a regression in either half shows as a diverging pair. Two of those cases exist
specifically to guard the "must not touch" half: the darkening *ratio* between the regimes is asserted
constant across a matched-elevation sweep (any vacuum-derived softening would make it vary with
elevation), and `PenumbraMath`'s public surface is asserted by reflection to admit no `inVacuum` at
all — which under §18's convention is what a vacuum-aware pure function would be required to take.

Live verification is blocked on `Jeffrharr/RimWorldTestHarness#17`, exactly as §18b is: scenarios
cannot currently reach an orbital map, because `SetTile`/`SetBiome` target planet-surface tiles and
`OrbitLayer.CanSelectLayer` refuses the layer unless a world object already exists on it. **Nothing
here has been validated in a running game.** The plan once unblocked is an off/on A/B of
`vacuum_shadow_contrast` on an orbital map at high sun, plus a probe read of the umbra — the existing
`moon_shadow_render` probe already reads exactly the right value (`MatBases.SunShadow.color.r`, the
final composed shadow colour), per the project rule that pixel centroids lie and probes give numbers.
Expected readings at full sun on a new-moon orbital map: **off ~0.74**, **on ~0.03**.

## 18d. Limb refraction (`LimbRefractionMath` / `Patch_LimbRefraction`)

The one item in the epic that **adds** a look rather than removing one, and the thing that makes §18a
worth doing rather than merely correct. §18a takes the ground twilight off a space map and leaves a
hard step at the terminator; this is what physically belongs there instead.

**The phenomenon.** Nothing sits between an orbital platform and the sun, so the sun does not dim at
all as it descends — right up until the planet gets in the way. For the last couple of degrees before
occultation the light still reaching the platform has grazed the planet's atmospheric limb, taking the
longest possible path through air, and Rayleigh scattering has removed everything but the red end. It
is the same geometry that makes a totally eclipsed moon copper: the platform is briefly inside the
planet's own penumbra, lit only by light bent through its ring of sunset. Then the solid limb covers
the disc and there is nothing left but planetshine.

**The anchors — the one arbitrary thing in the subsystem, stated once.** All of the geometry below
needs a planet radius and an atmospheric depth, and RimWorld supplies neither. Rather than tune the
result until it looked right, §18d picks Earth-like values **once**, names them here, and derives
everything else:

| # | Anchor | Value | Provenance |
|---|---|---|---|
| 1 | Planet radius | 6371 km | Earth's mean radius. RimWorld states no planet size; `PlanetLayerSettings.radius` (100 surface / 130 orbit) is a render-space unit for drawing the globe. |
| 2 | Platform altitude | 200 km | `PlanetLayerDef.elevationString` on Odyssey's `OrbitLayer`. **This is an anchor, not a lookup** — see below. |
| 3 | Refracting shell depth | 50 km | Troposphere + stratosphere; the part of the air thick enough to bend and redden a grazing ray. |
| 4 | Atmospheric scale height | 8 km | Earth's. Sets the ramp's *shape*, not just its width. Consistent with anchor 3: 50 km is 6.25 scale heights, where density is ~0.2% of sea level. |
| 5 | Solar angular diameter | 0.53° | Earth's. Smears both ends of the band by half a disc. |

**Why altitude is an anchor and not a lookup.** `PlanetLayerDef.elevationString` is declared
`[MustTranslate] public string elevationString = "{0}m"` — a **display string** for the world-map UI
("200km" in Odyssey's `OrbitLayer`), and nothing in the game parses a number back out of it. The
obvious alternative is a trap and is rejected explicitly: `PlanetLayerSettings.extraCameraAltitude` is
a **camera parameter**, sitting in the same `IExposable` struct as `origin`, `viewAngle`,
`subdivisions` and `backgroundWorldCameraOffset`, and Odyssey's orbit settings give it `300` against a
sphere `radius` of `130` — over two planetary radii of pull-back. Read as physical altitude that would
be ~15 000 km, not 200. Both facts are pinned by `ApiCompatibilityTests`
(`PlanetLayerDef_ElevationString_IsStillOnlyADisplayString`,
`PlanetLayerSettings_ExtraCameraAltitude_IsStillACameraParameterWeDoNotRead`), written to fail loudly
if RimWorld ever grows a real numeric altitude — at which point §18d should derive from it and stop
anchoring.

**What follows from the anchors.** Three differences from ground twilight, all derived:

| Quantity | Formula | Value |
|---|---|---|
| Horizon dip (solid limb) | `acos(R / (R+h))` | **14.172°** |
| Shell-top dip | `acos((R+d) / (R+h))` | 12.266° |
| Limb slant range | `sqrt((R+h)² − R²)` | 1608.9 km |
| Shell arc | dip − shell-top dip | **1.907°** |
| Band width | shell arc + solar disc | **2.437°** |
| Band duration | width ÷ 15°/h | **9.75 min** (equatorial; a lower bound) |
| Band top / bottom | `−(shellDip − r☉)` / `−(dip + r☉)` | **−12.001° / −14.437°** |
| Sunlit overshoot past the ground | −0.83° − band bottom | **13.607° ≈ 54.4 min** |

1. **The horizon is depressed** (14.17°), so the platform stays in full sun the better part of an hour
   past the ground below it, at both ends of the day. Implemented as `SunClockMath.GlowFromElevation`
   evaluated at `elevation + dip` — §14's existing three-anchor brightness curve re-referenced from the
   ground's horizon to the platform's. That single addition *is* consequence 1, which is what makes
   this subsystem "a curve swap in a ramp we already own" rather than a second brightness model.
2. **The ramp is short**: 2.437° against a sea-level twilight running from −0.83° to −18° (17.17°).
   About **one-seventh** the angular width, pinned against `NightRadianceMath`'s own two anchors so the
   comparison is to the mod's real twilight rather than to a textbook number.
3. **The ramp is red, then it stops.** Extinction is exponential in the ray's tangent altitude, so the
   colour barely moves for the first half of the band and then collapses. The "step" to the night floor
   is not a discontinuity anyone inserted; it is what an exponential looks like when the band runs out.

**Exact vs. linearised.** The estimate the design was reasoned from — `atan(50 / 1609) = 1.780°` —
is the first-order term of the exact difference of tangent lines (the impact parameter is
`(R+h)·cos δ`, whose derivative at the dip is exactly the limb slant range). It runs 7% low because
`sin` grows across the band. The shipped code uses the exact form, 1.907°;
`LinearisedShellArc_MatchesExactFormToWithinAnEighthOfADegree` pins the 0.127° gap so the estimate
stays a documented cross-check rather than a silent divergence.

**Which side of the dip the band sits on.** Worth stating because the issue's prose reads the other
way round ("full sun until −14.2°, then a ramp"). The dip *is* the solid limb by construction, so the
refraction band necessarily sits **above** it and the step to the floor happens **at** it: full sun to
−12.0°, red ramp to −14.4°, then nothing. Symmetric on sunrise, for free — elevation is the only input
and `Formulas.SolarElevationDegrees` is even in hour angle, so there is no dusk-only branch to get
backwards.

**Colour.** Per-channel transmission is Beer-Lambert on `tau(z) = tau_grazing · exp(−z/H)`, with
`tau_grazing = tau_zenith · sqrt(2πR/H)` — the standard grazing-column amplification, **70.7×** here,
and the same factor that reddens an eclipsed moon. Zenith optical depth uses the published Rayleigh
fit `0.0088·λ^-4.15`. At the limb that leaves red at 2.4%, green at 0.06% and blue at 4×10⁻⁸, with **no
per-channel tuning anywhere**. The tint is carried as a *direction* (renormalised so the brightest
channel is 1), exactly as `BloodMoonMath.CrimsonTint` does, so it can never double as a dimmer.

The tint's **strength is a spike, not a ramp**: the spectral shift (`1 − normalised green`) scaled by
`SolarDiscVisibleFraction`, peaking at 0.789 around −13.95° and returning to zero at both ends of the
band. The second factor is load-bearing. Without it the strength would still be ~0.98 the instant the
disc vanished, and because colour and glow are separate `SkyTarget` fields the tint would then sit on
the sky for the whole of orbital night — a blood-red darkness held over from a sun that had already
set. With it, the colour ends exactly where the light does, and what colour the night actually is stays
entirely §18b's planetshine question. The spike is also the honest shape: this is a *flash*, cut off
mid-deepening by the planet, not a sunset that fades.

**The planetshine floor is injected, not defined here.** `VacuumSkyGlow` takes it as a parameter and
`max()`es against it, for the same reason `NightRadianceMath.ApplyNightFloor` uses a max: a floor is a
floor, and the handover happens on its own wherever the exponential crosses it. §18b owns that number;
§18d's job ends at "the sun is gone". The adapter reads it from `NightRadiance.FloorGlowFor(map)` —
§18b's shared read, the same function §7 and §18e consume, so there is no ordering to get wrong and no
staleness.

Taking it as a parameter rather than a constant buys something concrete: **the floor is
moon-dependent** (0.0317 on a new moon, materially higher under a full one, since moonlight is a term
in it), so the elevation at which the ramp hands over to the floor *slides up and down the band with
the lunar cycle* without a single constant in §18d changing. Under a bright enough moon the last of
the limb light is genuinely outshone before it reaches its reddest, and the red spike becomes a
dark-moon spectacle. That is intended emergent behaviour and it is pinned
(`TheFloorIsMoonDependent_SoTheHandoverElevationMovesWithTheLunarCycle`), not a wobble to be tuned
out — it is the same way §7's floor swallows the tail of ground twilight on a surface map.

§18d does **not** call `VacuumRadianceMath.PlanetshineLux` or reason about planet-reflected light
anywhere. It consumes only the composed floor. This matters because #31 found that term is ~33 000 lux
*above* the horizon — four orders of magnitude off the near-zero value §18b measured at astronomical
twilight — and is negligible on the deck only by an occlusion argument (stationary orbits, so the deck
faces its tile's zenith and the platform's own structure is between it and the planet). §18d's band is
entirely below the horizon, and its sunlit-overshoot region is lit by the **direct** sun via
`SunClockMath.GlowFromElevation`, with no reflected term at all, so neither the magnitude nor the
occlusion argument enters here.

**Deliberately out of scope: the green/blue flash.** Real astronauts report it at the top edge of the
band, and it is genuinely there. It is also a *pointing* phenomenon — you see it because you are
looking along the limb at one spot on the planet's edge. Our lighting is full-map ambient with no view
direction at all, so there is no honest presentation of it here, and a green tint on the whole sky
would be decoration pretending to be physics.

**Not a `GameCondition`.** This is the daily planet-occultation — ordinary orbital night, once per game
day, a pure function of sun elevation. §10a owns eclipses (moon transits the sun, once every few game
years) and the two share no code path. The boundary is exact: *platform crosses into the planet's
shadow* = daily = here; *moon crosses the sun* = rare = there.

### Who owns `.glow` on a vacuum map

Settled here rather than deferred, because it turned out to be a correctness question and not a
tidiness one. **§18d owns it outright, and §7's `Patch_NightRadiance` stands down on vacuum maps.**

The overlap is real. `NightRadianceMath.ApplyNightFloor` blends toward the floor with a weight that
ramps from −0.83° to −18° — the **sea-level twilight span**, encoding "how far through the
atmosphere's own dusk are we". A platform has no dusk to be partway through: it is in full sun until
−12.0° and fully in the planet's shadow by −14.4°, and in between it is lit by refracted light that
ramp knows nothing about. Left running it would blend the sky 65–79% toward the night floor straight
through the limb band, and — the damaging case — 24% of the way there at −5°, where the platform is
in broad daylight. Measured against §18d's own numbers that is **0.0077 where the answer is 0.652**:
§7's ramp would erase the depressed horizon, the single largest visible consequence of the subsystem.
So this is not two patches producing slightly different numbers; it is one patch applying a curve with
no referent 200 km up.

Nothing is lost by standing down. `VacuumSkyGlow` is a **total** answer at every elevation — it builds
the sunlit term from scratch rather than scaling whatever arrived, and `max()`es it against the same
`NightRadiance.FloorGlowFor(map)` §7 would have used — so the floor still reaches the sky, through one
writer instead of two postfixes racing on the same field. At and below −18°, where §7's weight has
reached 1, the two agree exactly; that equality is pinned
(`VacuumSkyGlow_AtDeepNightMatchesWhatTheSeaLevelNightFloorWouldHaveProduced`), as is the −5° divergence
that justifies the gate (`SectionSevensRampWouldHaveErasedTheDepressedHorizon`), so the gate cannot be
removed quietly.

This is the mod's usual **one owner per field** discipline (§6a states it for `colors.shadow`), and it
did not need anything in §18b to change — §18b's floor value is correct and is consumed as-is. The
fix belongs on the §7 side of the seam because it is §7's *elevation ramp*, not §18b's *floor*, that
has no meaning in vacuum.

**A gameplay-visible consequence, stated plainly.** Because glow drives plant growth and solar output,
extending the platform's sunlit day by ~54 minutes at each end is not purely cosmetic — it is ~1.8 h of
extra solar generation per day on an orbital map. It is accepted rather than suppressed because it is
the direct geometric truth (§8's epic explicitly calls vanilla running a full ground lighting cycle
200 km up "exactly the thing worth reacting to"), because it is confined to `inVacuum`, and because
space maps have no agriculture for it to distort. It is the one place in §18 where a derived visual
consequence reaches gameplay, and it is worth a reviewer's eye rather than a footnote.

**Other conflict risk.** `Patch_LimbRefraction` is a third postfix on `WeatherWorker.CurSkyTarget`
alongside §2 and §7. It writes `.glow` and `.colors` on vacuum maps only — every surface map sees a
strict no-op at every elevation, which the paired sea-level column in `LimbRefractionMathTests` pins
case by case. Against §2 there is no overlap even on a vacuum map: §2's warm band is over by −6° and
§18a zeroes it there regardless, while §18d's band opens at −12.0°.

## 18e. Eclipse response in vacuum (`VacuumEclipseMath` / `Patch_EclipseVacuumSky`)

**This section reverses a line in epic #8.** The epic's work table said natural-eclipse generation
"should not fire on vacuum maps rather than firing with meaningless geometry". That was written
assuming an orbital platform's sun motion is an orbital period. It is not — see §18's stationary-orbit
finding: `PlanetLayer.LongLatOf` derives lat/long from a static tile centre and nothing anywhere gives
an orbit tile a period, a phase, or any motion. An orbital tile therefore has a fixed lat/long and
sees the same sky as the surface tile below it, so a new moon at a node transits the sun for the
platform at the same instant, for the same duration, and with the same impact parameter as for the
ground beneath it.

**§10a's geometry is valid in orbit and keeps firing.** `EclipseMath`, `MoonMath`'s inclination/nodal
model, `MoonMath.DefaultNodalPeriodDays` and the ~one-every-few-game-years cadence test are all
untouched by this subsystem, and the cadence target is unchanged in vacuum.

**Problem.** What *is* wrong in vacuum is the response, and it is one physical fact with two
consequences: there is nothing left to scatter light into the umbra. Standing at sea level inside the
moon's shadow, most of the sky you can see is *not* in that shadow and goes on scattering sunlight
down onto you the whole time. That is why a total eclipse at sea level is a deep blue-grey gloom
rather than night, and it is exactly what vanilla's own `GameCondition_NoSunlight.EclipseSkyColors`
encodes — a wan `(0.482, 0.603, 0.682)`, Rec. 709 luma **0.583**, i.e. 58% of full sky brightness at
totality no matter what the glow says. At 200 km there is no scattering medium at all, so that
pedestal should not be there.

**Approach.** One postfix on `GameCondition_NoSunlight.SkyTarget`, deliberately separate from §10's
existing postfix on `SkyTargetLerpFactor`. The two answer different halves of the same event:

| patch | method | answers | in vacuum |
|---|---|---|---|
| `Patch_EclipseDarkening` (§10) | `SkyTargetLerpFactor` | *how fast* the sky reaches the umbra | unchanged — pure disc-overlap geometry, valid in orbit |
| `Patch_EclipseVacuumSky` (§18e) | `SkyTarget` | *what the umbra is* | rewritten |

Splitting it this way is what makes §18e structurally incapable of moving when an eclipse fires or
how long it lasts. Two channels are rewritten and the rest of the target is left alone:

| channel | sea level | vacuum | why |
|---|---|---|---|
| `glow` | vanilla's flat `0` | **the §18b night floor** (0.0317) | the umbra is lit by the night sources and nothing else |
| `colors.sky` / `colors.overlay` | ×1 (exact identity) | ×0.167 | §7a's own glow→screen curve at that floor |
| `colors.shadow` | untouched | untouched | shadow contrast in vacuum is #31/§18c's; two branches writing it is the drift #30 exists to prevent |
| `colors.saturation` | untouched | untouched | colour handling on vacuum maps is #29/§18a's |

**Totality goes near-black, and the umbral minimum IS the shared floor.** The glow bottoms out at
`NightRadianceMath.NightFloorGlow` — #30's function, read through `NightRadiance.FloorGlowFor(map)`,
the same one §7 blends the night sky toward and the same one #31 bottoms its shadows out at. Not a
fraction of it, not a second constant: `VacuumEclipseMathTests` asserts the identity against the
shared function rather than against a literal, so a retune of the night budget carries through
instead of being pinned to a stale copy. #31 binds to the same function from the shadow side, so the
two provably agree.

Two results that look like bugs and are not. First, the vacuum umbra is a touch *brighter* than
vanilla's, whose target glow is a flat 0 — totality in orbit is starlit, not switched off, and 0 was
never physical. The near-black look comes from the colour channel, not from driving gameplay light to
nothing. Second, the floor tracks the moon, so an eclipse under a full moon would bottom out higher —
except that a solar eclipse happens at new moon *by definition*, so the moonlight term is ~0 and the
vacuum umbra lands on unextinguished starlight. The moon blocking the sun is, correctly, showing us
its unlit face.

The colour scale reuses `NightRadianceMath.OverlayBrightnessFactor` — §7a's own glow→screen curve —
rather than a new mapping, so "in vacuum, totality looks like night" is enforced by construction
rather than by two constants that happen to match today. The player's `MinNightBrightness`
playability clamp rides along inside it, which matters more here than anywhere else: totality is the
one darkness a player cannot walk out of.

**Ingress and egress harden, and there is no hardening knob.** With the pedestal gone, almost nothing
is left in the sky that did not come straight through the uncovered part of the solar disc, so
brightness follows the covered fraction far more literally. That falls out of removing the pedestal —
a tuned steepening constant would be a look choice pretending to be physics.

Quantified rather than asserted. `VacuumEclipseMath.CoverageTrackingError` is the mean absolute
deviation, over a whole transit, between how far the sky has actually dimmed and the fraction of the
solar disc covered. A response driven by disc overlap and nothing else scores exactly 0; every unit of
score is light in the umbra that did not come through the sun. Sampling a central transit through
§10a's own coverage ramp:

| | umbral sky brightness | tracking error |
|---|---|---|
| sea level | 0.583 | 0.583 × mean coverage |
| vacuum | 0.583 × 0.167 = **0.097** | 0.097 × mean coverage |

— a factor of **6.0** tighter, swept across magnitudes 0.2 → 1.0 so the claim covers grazing partials
(which are all ingress and egress) and not just totality. The test asserts a 4× margin, leaving room
for the night floor to be retuned without leaving room for the claim to become decorative.

**Lunar parallax stays ignored, and this is where the argument is written down.** A 200 km platform is
displaced from the ground observer by at most 200 km against a lunar distance of ~384,400 km, so the
moon shifts by at most `atan(200 / 384400) = 0.0298°` — about **6% of a lunar disc diameter**
(2 × 0.274° = 0.548°). That is not zero. But §6 already accepts a flat-ecliptic approximation whose
own error dwarfs it, and the eclipse's impact parameter comes from the moon's ecliptic latitude at
new moon, which the nodal model does not produce to anything like 0.03° precision in the first place.
A parallax term would be false precision bolted onto an approximation two orders of magnitude
coarser. The existing simplification carries over unchanged; it needed the argument written down, not
a new model.

**The boundary that matters: orbital night is not an eclipse.** A platform crosses into the *planet's*
shadow once per day. That is ordinary orbital night. It belongs to the sun clock and #32's
limb-refraction ramp (§18d), it must never be modelled as a `GameCondition`, and it must never feed
the eclipse cadence — a daily event entering a counter tuned for one every few years would destroy
both. §18e only ever describes the moon transiting the sun.

| event | cadence | owner |
|---|---|---|
| platform crosses into planet shadow | daily | sun clock + limb-refraction ramp (§18d) |
| moon transits the sun | ~one every few game years | §10a natural eclipses, §18e response |

**Out of scope: the corona.** A visible corona during totality would be the physically correct payoff
of a no-atmosphere eclipse, and it is genuinely a thing only visible from up there. It is a
*rendering* feature rather than a lighting one — a new drawn body, not a curve — so it is deliberately
not built here and belongs in its own issue if it is ever wanted.

**Conflict risk.** Low, and narrower than §10's existing patch. `Patch_EclipseVacuumSky` postfixes a
method no other CelestialLighting patch touches, and it is gated three ways: the "Eclipse effects"
master, `def == GameConditionDefOf.Eclipse` (so the Royalty SunBlocker machine, which shares
`GameCondition_NoSunlight`, stays vanilla), and a null-biome guard for pocket maps mid-generation. On
every planet-surface map it is a *provable* no-op rather than an intended one: the sea-level arm of
`UmbralSkyBrightnessScale` returns exactly `1f` and `UmbralGlow` passes vanilla's value straight
through, which `SeaLevelScale_IsExactlyOne_SoTheAdapterCannotDriftOnSurfaceMaps` pins bit-exactly.
No `ModsConfig.OdysseyActive` gate and no soft reference: `inVacuum` is a field on base
`RimWorld.BiomeDef`, so this compiles and reads `false` with Odyssey uninstalled.

Because the rewrite goes through the condition's own `SkyTarget`, everything downstream that reads
`SkyManager.CurSkyGlow` — including Dub's Skylights — sees the corrected umbra with no separate compat
patch, exactly as §7 does.

**Verification.** Offline in `VacuumEclipseMathTests` (27 cases), plus three new
`ApiCompatibilityTests` pinning `GameCondition_NoSunlight.SkyTarget`'s nullable return,
`EclipseSkyColors` (the sea-level anchor the whole comparison is relative to) and
`SkyColorSet.LerpDarken` (which `EclipsedSkyBrightness` is an offline model of). The `eclipse_umbra_glow`
probe reports the live umbral target by calling the patched method, so a scenario can pin it against
`night_radiance`: on a vacuum map the two must read the *same* value, which is the whole §18e claim
in two numbers.

Live verification is blocked on `Jeffrharr/RimWorldTestHarness#17` — scenarios cannot currently reach
an orbital map, because `SetTile`/`SetBiome` target planet-surface tiles and
`OrbitLayer.CanSelectLayer` refuses the layer unless a world object already exists on it. Nothing here
has been validated in a running game. When the block lifts, the eclipse scenario must run
**standalone**: eclipse scenarios are `GameCondition`-driven and the harness only reloads between
suites for MAP residue, so a lingering `Eclipse` condition contaminates whatever runs next.

## 19. Polar night blue — ozone (Chappuis) twilight (`OzoneTwilightMath` / `Patch_PolarNightBlue`)

The mod renders warm twilight (§2, §8) and a cool grey Purkinje shift (§9), but nothing reproduced
the most recognisable feature of high-latitude winter: the **deep blue cast** that sits over the
landscape for hours or whole days.

**Why it is blue — not Rayleigh.** Rayleigh scattering is daytime blue, and it is §8's model. With
the sun below the horizon the only light reaching the ground has crossed a near-horizontal path of
tens to hundreds of km through the stratospheric ozone layer. Ozone's **Chappuis absorption band**
eats 450–780 nm, peaking at 603 nm — the orange/green middle of the visible spectrum — and what
survives is the short-wavelength tail. The effect is strong enough to have driven a real
evolutionary adaptation: Arctic reindeer seasonally tune a photonic tapetum for it.

### Why there is no latitude term

This is the first question any reader asks, so it is the first thing the file's header answers.

The absorbing column is set by the sun's altitude relative to the ozone layer, not by where the
observer stands: a sun at −7.2° presents the same slant path at Svalbard and at Quito. What latitude
changes is **dwell time**. Keying on elevation alone is therefore not a simplification but the
correct model, and it is strictly better than a latitude term:

- Polar night emerges with no polar special case anywhere in the code.
- The equator keeps its real, brief blue hour instead of being artificially zeroed.
- It tracks §14's sun clock and Realistic Axial Tilt for free, because dwell is a property of
  `SolarPosition.ElevationForMap` rather than a constant we wrote down.
- A `Formulas.LatitudeStrength` factor would double-count — latitude already enters via elevation.

**What actually governs dwell is the daily elevation swing**, and it is worth writing down because
the intuition is misleading. The swing is closed-form:

```
swing = maxElevation - minElevation = 180 - |lat - decl| - |lat + decl|
```

At the equator that is 180° — the sun rips through the band. At latitude 78 in midwinter it is still
24°, so the band is occupied around midday and empty at midnight: latitude 78 does **not** hold the
blue all day, despite being deep inside the polar-night latitudes. The swing collapses to 0 only at
the pole itself, where elevation simply *equals* declination and the sun can sit at −9° for days. So
the true "blue for the entire day" case is very high latitude with a declination that parks the
whole range inside the band — e.g. latitude 88 at day 11, whose range is [−11.5°, −7.5°] and sits
entirely within the plateau. That tile is what the live scenario probes.

### …and why the ozone *column* is nevertheless latitude-keyed (issue #82)

**This is a different factor, not the section above reversed.** Read the two together or the code
will look like it quietly overturned a settled decision.

Beer–Lambert is `τ = σ · N_column · airmass`, three independent factors:

| factor | what it is | varies with | how it is modelled |
|---|---|---|---|
| `σ` | how strongly one molecule absorbs | wavelength | published Chappuis cross-sections |
| `airmass` | how LONG the path through the layer is | sun elevation | `SlantAirmass` — **the geometry the section above is about** |
| `N_column` | how MUCH absorber sits along that path | latitude (and season) | `OzoneColumnForLatitude` |

The section above is an argument about the middle row and it stands untouched: path length is set by
the sun's altitude relative to the layer, so it is elevation-keyed and nothing else, and putting
`Formulas.LatitudeStrength` on it would double-count because latitude already reaches it through
elevation. `N_column` is the third row. It is not a path length — it is the *density of absorber
along* that path — and on Earth it is not globally uniform: the **Brewer–Dobson circulation** lifts
ozone over the tropics and transports it poleward, so real total columns run ~260 DU over the tropics
against ~380–420 DU at high latitudes in spring. **Same slant path, more molecules per unit length.**

Scaling the column by latitude cannot double-count, because before #82 nothing in the file expressed
it at all: `σ` came from published tables and `airmass` came from the sun, while `N_column` sat as a
single hardcoded 300 DU. This makes the third factor as honest as the other two.

**The existing calibration is preserved as the curve's midpoint.** 300 DU is the global mean *and*
roughly the mid-latitude value, which is the accident that lets the curve be added without
recalibrating anything — `OzoneColumnForLatitude(45°)` returns `OzoneColumn` exactly, so every
measured number above still describes a mid-latitude map:

| latitude | column | red attenuated on ground @ −7.2°, blend 0.45 | vs before |
|---|---|---|---|
| 0° | 260 DU | 20.8% | shallower — the brief equatorial blue hour, kept and still visible |
| 45° (pivot) | **300 DU** | **24.2%** | **unchanged** |
| 70° | 385 DU | 29.8% | where polar night actually happens |
| 88° | 420 DU | 31.7% | the live scenario's tile |

`MaxSlantAirmass` is untouched — it remains the honest calibration knob this section already
documents, and the column is deliberately *not* a second one.

**Why sin⁴|latitude| for the shape.** Two constraints leave little room. Hemispheric symmetry says
the column must be an **even** function of latitude and smooth across the equator, which rules out a
straight `|latitude|` ramp — that puts a cusp at the equator, and the gradient reversing direction
at a hard line is not something any physical process draws. Both ends are also observationally flat:
the tropical column barely moves out to ~20°, and the poleward pile-up saturates rather than spiking
at the pole. sin⁴ has zero derivative at both ends and its steepest gradient near 55°, which is where
the real subtropical-to-midlatitude gradient lives. It is a fit to the *shape* of the climatology,
not a derivation from the circulation.

The polar value is **not a third free parameter**: with a sin⁴ profile, 260 DU at the equator and
300 DU at 45° (where sin⁴ = ¼) force `260 + 4·(300 − 260) = 420` DU at the pole. That it lands inside
the observed Arctic-spring range is a check on the shape, not an input to it.

**What is deliberately not done.** No latitude threshold, no polar special case, and **no change to
`BandStrength` or `SlantAirmass`** — neither gained a latitude argument, which is the mechanical
statement of the distinction above. The band still opens and closes on elevation alone at every
latitude, so polar night still emerges from dwell time and the equator keeps its blue hour, just a
shallower one.

**Independent of site altitude and air quality, permanently.** The ozone layer sits at 20–30 km,
above the bulk atmosphere and far above the boundary layer, so a mountain map and a polluted map
cross the *same* ozone column as a sea-level clean one. Site altitude and aerosol loading belong to
§8; §19 must never grow a second copy of them, and the invariant is enforced within §19's own inputs
as a signature guard (`OzoneTwilightMath_TakesNoSiteAltitudeOrAerosolInput`) rather than as a value
comparison, since there is deliberately no altitude input here to vary.

**The seasonal half is deferred, not forgotten.** The high-latitude column peaks in *spring*, not
midwinter, and `SolarPosition` already carries declination so it is available. It is not shipped
because polar night is exactly when the sun is lowest and spring is when it returns — a seasonal term
would move the extra depth *out* of the window this subsystem exists to serve. Revisit it from the
live scenario, not from the formula.

**Bearing on #78** (polar night blue reads too weak in play). This delivers ~30% more optical depth
at the latitudes the complaint comes from and *less* in the tropics, from a cited mechanism rather
than a tuned multiplier — which is strictly better-behaved than the global strength bump #78
contemplates, since a global bump would also over-blue the equator where this section explicitly
wants restraint. It is unlikely to close #78 by itself: on screen the polar peak's red attenuation
moves 24.2% → 31.7%, a real but not dramatic step, and the blend strength and the §7a floor remain
the larger levers on *perceived* strength. Treat it as shrinking the gap #78 has to close, and A/B
the two before #78 picks its fix.

### The correction that the subsystem turns on: model the notch, not a colour temperature

The obvious design is `SkyColorTemperature.BlackbodyToRgb(20000)` from the published ~20,000 K CCT
measured at −7.2°, reusing §8's tested code. **It does not work, and the arithmetic says so before
any code runs.**

`MatBases.LightOverlay.color` **multiplies** the scene — `NightDesaturationMath`'s header records
this as a measured dead end for §9, where a tint close to vanilla's night sky moved ground
saturation by 0.001 and "the effect was, measurably, not there". A multiply shifts hue only by
*attenuating* channels, and vanilla's night sky is already almost exactly as blue as that blackbody:

| | R/B | G/B |
|---|---|---|
| vanilla Clear night sky (0.482, 0.603, 0.682) | 0.707 | 0.884 |
| Planckian 20,000 K (0.669, 0.778, 1.000) | 0.669 | 0.778 |

There is nothing to move toward. Lerping the sky colour **fully** to that target attenuates red by
**5.3%** — invisible, and §9's dead end repeating a third time.

The reason is physical: polar twilight is not a blackbody. It is sunlight with a broad absorption
notch cut out of it, and a CCT is a poor single-number summary of a notched spectrum. So we model
the notch directly, Beer–Lambert per channel (`T = exp(−σ·N·m)`) using published Chappuis
cross-sections sampled at the sRGB channel centres — R on the 603 nm peak, G on the 575 nm flank,
B inside the Huggins–Chappuis minimum where ozone is effectively transparent:

At the pivot latitude (45°), which since issue #82 is where `OzoneColumnForLatitude` reproduces the
300 DU constant these numbers were measured against — see the column subsection above for the same
table swept across latitude instead of elevation:

| airmass | transmission RGB | R/B | red attenuated on ground @ blend 0.45 |
|---|---|---|---|
| 15 (≈ −4°, onset) | 0.537, 0.639, 1.000 | 0.537 | 18.3% |
| 27 (≈ −7.2°, peak) | 0.327, 0.447, 1.000 | 0.327 | **24.2%** |
| 45 (≈ −12°, plateau) | 0.155, 0.261, 1.000 | 0.155 | 35.1% |

Five times the blackbody's effect at *half* the blend, and it **deepens with elevation for free**
because the notch cuts deeper as the slant path lengthens — a progression a fixed colour cannot
express. `GroundRedAttenuation_ExceedsVisibleThreshold` is the standing guard: it reproduces the
multiply against vanilla's night sky and fails below 20%.

**On the 20,000 K figure**, since a reader will try to check us against it: our model is deliberately
*more* saturated, giving R/B 0.33 at −7.2° where the blackbody gives 0.67. That is not a discrepancy
to fix. CCT is found by projecting onto the nearest point of the Planckian locus, and a notched
spectrum sits well off the locus toward higher saturation, so its CCT necessarily understates how
coloured it looks. Reproducing it exactly would mean reproducing the very desaturation that made the
blackbody version invisible. `MaxSlantAirmass` is the honest calibration knob.

**Why not extend §8.** Beyond being a different model, §8 has two load-bearing invariants under test
that Chappuis inverts: `ColorTemperatureKelvin_IsMonotonicNonDecreasing_AsSunClimbs` (a 20,000 K
spike below a 2,000 K horizon fails it) and `BlackbodyToRgb_StaysWarm_AcrossOurWholeRange`, which
asserts R ≥ G ≥ B where Chappuis is B > G > R. Different vacuum semantics too: §8 still has an
honest unreddened colour to pin, whereas ozone twilight has no vacuum analogue at all.

### The reachability gate

A cheap correctness-preserving early-out, so the adapter skips the three `exp()` calls on any day
the band cannot open. **Not a latitude threshold** — every latitude crosses −4°..−18° twice a day,
so gating on latitude alone would delete the equator's blue hour, the exact outcome the
elevation-only model exists to avoid. It gates on latitude *and* season, via the day's closed-form
elevation extremes (`90 − |lat − decl|` and `|lat + decl| − 90`), catching only the two genuinely
unreachable states: **polar day** (the sun never dips to −4°) and **true polar night** above ~84.5°
(never climbs to −18°, so there is no sunlight in the ozone path at all and §7 owns the sky).

Because `SolarPosition.InputsForMap` is memoised per (map, frame) and already carries latitude and
declination, the gate costs nothing to feed. `CanReachBandToday_NeverSkipsAReachableDay` sweeps
181 × 47 (latitude, declination) pairs and, for every one the gate rejects, samples the whole day
through `Formulas.SolarElevationDegrees` asserting the band never opens — with a non-vacuity check,
because a sign error that degenerated the gate to "always run" would otherwise pass green.

### The brightness floor, and why it lives in §7a

Real polar twilight is dim but emphatically not black — snow reads blue because scattered skylight
still lands on it. Without a floor the blue is multiplied straight into darkness on any preset with
a low `minNightBrightness`.

The floor is expressed by raising the `minBrightness` that `Patch_PitchBlackOverlay` already feeds
to `NightRadianceMath.OverlayBrightnessFactor`. That is **visual-only by construction**, not by
care: `OverlayBrightnessFactor` feeds nothing but `MatBases.LightOverlay.color` and
`FogOfWar.color`, so `GlowGrid`, plant growth, solar output and Dub's Skylights never see it. This
was the explicit constraint — a parallel floor that touches no gameplay value.

Placing it there is forced rather than stylistic. Three alternatives, each rejected on its own
ground:

- **Writing `.glow` / `ForceSetCurSkyGlow`** — drives every gameplay consumer above. Ruled out.
- **A second `SkyManagerUpdate` postfix lightening the overlay** — two of our own patches then
  fight over one global material with order-dependent results; lerping toward *white* would also
  desaturate the blue we just added; and it risks re-enabling the overlay on `disableSkyLighting`
  biomes §7a deliberately skips.
- **Brightening the target colour upstream** — §7a runs later on the composed material and lerps
  toward opaque black, so upstream brightness is multiplied away. **§7a's darkening is the last
  word on screen brightness, so any floor must be expressed in §7a's terms.**

`DefaultOverlayFloor = 0.30` is derived: at −7.2° with §7 running, composed glow is ≈0.014 and
`OverlayBrightnessFactor` returns ≈0.076 — 7% of vanilla overlay brightness is functionally black.
0.30 keeps the band readable, sits under a full moon's 1.0, and sits under Cinematic's 0.50, so that
preset is unaffected by construction. §7a's existing `NightRadiance && PitchBlackNights` gate is
already correct: if either is off nothing darkens, so there is nothing to floor.

The same normalisation logic explains why the colour arm must **not** brighten: `ChappuisTransmission`
is normalised to a maximum channel of 1 and the adapter rescales it to the source colour's own
brightest channel, so only channel *ratios* move. Skipping that rescale would drag everything toward
white, smuggling a brightness rise into a patch documented as colour-only — and §7a would multiply
it away anyway.

### Composition and ordering

With §19 there are now several postfixes on `WeatherWorker.CurSkyTarget`. §7 writes only `.glow`;
the rest read and write only `.colors`.

- **vs §8 and §2 (warm).** §8 dies at `NightFadeFloorDegrees` (−6) and §2's civil-twilight
  persistence dies at the same −6, while our blue starts at −4. The 2° overlap is deliberate: real
  dusk has a warm band low in the west under an already-blue vault.
- **vs §9 (Purkinje).** §19 stacks with the cool-grey tint deliberately, with no cross-subsystem
  suppression. They model different things — §9 is the eye losing colour discrimination as rods take
  over, §19 is the sky genuinely being blue — and real polar twilight is both at once.

**No `HarmonyPriority`.** Successive `Color.Lerp`s are not commutative; the error is
`a·b·(B − A)` per channel. `WarmAndBlue_AreNeverBothAtFullStrength` holds the warm×blue product
under 0.10 across the whole overlap, and against §9 at peak strengths the bound is ~0.063 (≈16/255)
— a subtle hue difference, not an on/off one. This matches the precedent `Patch_WeatherDimming`'s
header sets, and intra-assembly order cannot be expressed anyway: all our patches share one Harmony
owner ID, so `[HarmonyAfter]` does not apply. If it ever *does* matter, the escape hatch is
composing §9's and §19's targets into a single blended target before one lerp — not a priority
attribute, which would only pick a winner rather than remove the ambiguity.

### Vanilla's ≥75° slew limit, and the trap it exposes

`WeatherWorker.CurSkyTarget` slew-rate-limits its threshold lerp to 0.002/frame at
`LongLatOf(map.Tile).y >= 75f` via `ClampLerpDelta`. It does not affect us: it rate-limits only the
base colour we lerp *from*, never our blend fraction, which we recompute from elevation. Our blue
arrives on schedule and the base merely crossfades more smoothly underneath.

**The trap:** `ClampLerpDelta` *mutates* `WeatherManager.prevSkyTargetLerp`/`currSkyTargetLerp` on
every call, at exactly the latitudes this subsystem targets, and vanilla already calls it twice per
update against one shared pair of fields. **A §19 probe must never call `CurSkyTarget`** — it would
advance vanilla's slew state as a side effect of being measured. `PolarNightBlueProbe` reads the
shared adapter and `OverlayBrightnessProbe` reads the finished material; neither goes near it.

### Vacuum

`inVacuum` is threaded into the pure layer as a parameter rather than early-returned in the adapter,
per §18a's rule, so no caller can extract a nonzero shape out of a vacuum. Unlike §8 — which still
pins an honest unreddened `ZenithKelvin` — ozone twilight has **no vacuum analogue whatsoever**: no
ozone, no slant path, no phenomenon. There is simply nothing to report, so the whole effect is zero.

### Verification

Offline: `OzoneTwilightMathTests` covers the trapezoid anchors, the plateau (the dwell-time
property — if someone simplifies the trapezoid back to a triangle, that test fails), continuity, the
vacuum gate, hue ordering and normalisation, the reachability gate with its non-vacuity check, the
floor table, its composition with `OverlayBrightnessFactor`, and the two visibility guards.

Issue #82 adds, all against `N_column` and none against the geometry: the pivot regression pin
(`OzoneColumnForLatitude(45°)` reproduces `OzoneColumn` to float precision, so the whole measured
record above still stands), the DU climatology table, monotonicity in `|latitude|` swept past 90° to
catch sin⁴ folding back, hemispheric symmetry, the notch deepening with latitude at *fixed*
elevation (the airmass held constant is what makes it a column test rather than a geometry one, with
a non-vacuity check that the two ends differ), the polar case clearing the 20% threshold **by more
than the pivot does**, the equatorial blue hour staying above that same threshold rather than merely
non-zero, and the two signature guards — no altitude/aerosol vocabulary anywhere in §19's inputs, and
`ChappuisTransmission(elevationDegrees, latitudeDegrees)` in that order, since two same-typed
degree arguments would transpose silently.

**Re-measured live, and the prediction was wrong in both directions.** `polar_night_blue.json`'s
latitude-88 pins were taken with the uniform column, and the arithmetic said `sky_overlay_warmth`
would land near −0.101 — inside the scenario's ±0.01 tolerance, so the old pins were left alone
rather than replaced by a prediction. The actual reading is **−0.1068**, which is *outside* that
tolerance: the closed-form estimate understated the shift by about half a tolerance because it
reasoned about the transmission ratio alone and not about how the normalised hue then composes
through §7a's overlay. Predict-then-check is the point; this is the check failing, and the pin now
carries the measured number.

`overlay_brightness` moved too, and the old claim that it could not was **wrong**: it read
**0.1299** against a pinned 0.1399, also just outside ±0.01. The reasoning behind "unaffected by
construction" was that neither `OverlayFloor` nor `BandStrength` mentions the column, which is true
and is not the whole story — the floor is a *lower bound* on a brightness that is then multiplied by
the normalised Chappuis hue, and a deeper notch pulls red and green further down while blue stays
pinned at 1 by the normalisation. So the composed overlay does get dimmer even though nothing in the
brightness path takes a column argument. This is not §19 smuggling in a brightness term — it is the
unavoidable luminance consequence of a hue that is defined by attenuating two channels — but it does
mean "colour-only" is a statement about which knobs exist, not a promise that measured luminance
holds still. Band-strength pins are genuinely unaffected; those really are envelope-only.

Live, and **measured** — these are the pinned values, not predictions:

`Tests/Scenarios/polar_night_blue.json` (latitude 88, day 11) reads band strength **1.0 at hours 0,
6, 12 and 18** — the sun never leaves the plateau, which is the dwell thesis in numeric form. The
gate then returns exactly 0 at the same latitude in the opposite season (day 41, midnight sun). The
A/B across the feature toggle, on the Realistic preset:

| probe | §19 off | §19 on | |
|---|---|---|---|
| `overlay_brightness` | 0.0481 | **0.1299** | the floor arm: 2.7× brighter screen |
| `sky_overlay_warmth` | −0.0154 | **−0.1068** | the colour arm: 6.9× more blue (more negative == bluer) |

**The A/B only shows up on Realistic.** Cinematic's `minNightBrightness` of 0.50 dwarfs the 0.30
floor, so on the shipped default preset the two frames are near-identical — the scenario flips
`realistic_preset` on and back off around the comparison.

`Tests/Scenarios/polar_night_blue_equator.json` (latitude 0, day 15) pins the same latitude-free
curve producing a *brief* window, and `sun_elevation` is pinned alongside each band value so that a
future §14 clock change reads as a clock change rather than a §19 regression:

| hour | elevation | band |
|---|---|---|
| 20.0 | +3.50° | 0 |
| 20.4 | −0.83° | 0 |
| 20.6 | −5.78° | 0.556 |
| 20.8 | −10.73° | **1.0** |
| 21.0 | −15.69° | 0.385 |
| 21.2 | −20.64° | 0 |

That is a window of roughly **34 minutes** (elevation −4° at ~20.53, −18° at ~21.09) against
latitude 88's full 24 hours — a ~42× dwell ratio out of a function with no latitude term, which is
the entire claim. The measured values match the offline math exactly (−5.78° → 0.556, −10.73° → 1.0,
−15.69° → 0.385).

**A note for whoever writes the next scenario here:** the first draft of the equatorial block probed
hours 18–20 and read 0 everywhere, which looked like a bug. It was not — RimWorld's clock does not
put sunset at 18:00, and at the equator the sun is still **+3.5° at hour 20**. Survey elevations with
the `sun_elevation` probe before choosing hours; a scenario sampled where the effect is defined to be
zero passes vacuously and proves nothing.

**The same trap caught the next scenario anyway, twice.** `Tests/Scenarios/ozone_column_latitude.json`
first captured latitudes 45 and 5 at hour 18, where the band is empty, so its feature-off and
feature-on frames were bit-identical and its "A/B" showed nothing. The re-survey that fixed it then
sampled *hourly* and still read 0 at every hour of latitude 45's afternoon — because outside the
poles the band is only about **0.8 h wide at latitude 45 and 0.6 h at latitude 5** (day 11), so a
one-hour grid straddles it completely. Two rules follow, and they are cheap: survey at **≤0.25 h**
resolution, and once the hour is chosen, **pin `sun_elevation` next to the effect probe in the
scenario itself** so a future §14 clock change fails loudly instead of silently re-emptying the
capture. The scenario now does both:

| latitude | day | hour | elevation | band |
|---|---|---|---|---|
| 88 | 11 | 0 | −11.53° | 1.0 |
| 45 | 11 | 20.55 | −11.67° | 1.0 |
| 5 | 11 | 20.75 | −11.78° | 1.0 |

Those three elevations were chosen to land within 0.25° of each other on purpose. `SlantAirmass` is
then equal to within 2% across all three captures, so the airmass half of `τ` is effectively held
constant and the only thing left varying between the frames is the column — which makes the
screenshot set a controlled comparison of *this* subsection's change rather than a picture of three
different geometries.

Both scenarios are deliberately **out of `core_design_suite.txt`** and verified standalone, matching
`sky_color_temperature.json`'s precedent. They set different latitudes, and the `SunClock` cache is
latitude-blind, so batching them behind another latitude-setting scenario is an unverified risk for
no benefit.

`ApiCompatibilityTests` needs no new assertions — `WeatherWorker.CurSkyTarget`, `SkyTarget.colors`,
`SkyColorSet.sky`/`.overlay`, `SkyManager.SkyManagerUpdate` and `MatBases.LightOverlay`/`FogOfWar`
are all already pinned by §2, §8 and §7a.

## Settings and presets

Two cross-cutting settings ideas that span the subsystems above:

- **Opinionated presets.** Ship a small number of named presets (e.g. "Realistic" vs
  "Cinematic/Pretty") that set the correlated knobs together — shadow length/strength (§1),
  desaturation strength (§9), weather dimming (§13), and the two minimum-brightness floors (outdoor
  §7, indoor §7b) — so most players pick one preset and never open a slider. Individual sliders
  remain for anyone who wants them.

  **§19's "Polar blue strength" is deliberately NOT in the bundle.** It is a per-effect intensity
  like `doorSkyLeak`, not one of the taste axes the six bundled knobs correlate along, and adding a
  seventh field would touch `PresetKnobs`, its constructor, both preset literals, `ApplyPreset` and
  `CelestialSettingsMathTests` for no gain. Promoting it later is mechanical if eye-tuning shows it
  correlates. It uses `LabeledSlider` rather than `AestheticSlider`, so moving it does not flip the
  preset radio to Custom. Note §19's *floor* interacts with presets anyway, in the good direction:
  inert under Cinematic's `0.50` (0.30 < 0.50) and load-bearing under Realistic's `0`, which is
  exactly the preset where the blue would otherwise crush to black.

  A bundle only carries knobs something actually reads. An earlier "night radiance floor" knob was
  persisted, given a slider, and never consumed by anything: §7's night brightness comes from the
  starlight/airglow/moonlight sum in `NightRadianceSettings`, and how black the *screen* goes is
  `minNightBrightness`. It was removed rather than wired up, because two settings competing to mean
  "how dark is night" is the confusing part, not the missing plumbing.

  **Cinematic is the shipped default**, and it sets both minimum-brightness floors to `0.50`.
  Rationale: the two floors compound — a sealed room under a moonless night is the darkest thing
  this mod can produce — so a first-run experience on Realistic's zeroes is a player staring at a
  black screen wondering if the mod is broken. Realistic keeps both at `0` and is one click away.
  The floors live in the preset bundle (not as standalone tunables) precisely because lifting one
  without the other still leaves interiors unreadable; pinning them equal is what
  `CelestialSettingsMathTests` asserts.
- **One floor per scope, and no more.** An earlier design had a *third* brightness knob: a map-wide
  "accessibility floor", opt-in by checkbox and by an optional hotkey, clamping displayed glow upward
  as the very last step. It was removed once Cinematic's own two floors shipped at 0.50 — three
  settings competing to answer "how dark may night get" is worse for a player than one clear answer
  per scope, and the two preset floors already cover both scopes (outdoor overlay §7a, roofed cells
  §7b) and reach interiors, which glow-lifting never could. What it cost to remove is worth recording:
  a Harmony postfix at `Priority.Last` on `SkyManagerUpdate`, an unbound `KeyBindingDef` with its
  `GameComponent`, a pure `BrightnessFloorMath.Apply` clamp, a live probe, and the two-knob
  reconciliation (`EffectiveMinBrightness` / `EffectiveIndoorFloor`) that existed *only* because two
  floors could disagree.

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

`SectionLayer_SunShadows` is a leaf, internal, non-subclassed vanilla type. We Prefix exactly one of
its methods, `Regenerate()` (`Patch_ShadowMeshPerimeter`) — low risk of another mod patching that
exact method, but if one does, Harmony will only run whichever prefix returns `true` from
`__runOriginal` handling last (both returning `false` means only one prefix's replacement actually
runs). `DrawLayer()` used to be Prefixed here as well, by §3's per-draw shadow tilt; that patch and
the feature behind it are both gone, so vanilla's `DrawLayer` runs untouched and the sections batch
again. The one remaining touch on this type is a Postfix on its constructor
(`Patch_ShadowRoofInvalidation`), which only ORs a bit into a public `ulong` and cannot suppress
anything.

One mod in this setup does: **Perspective: Eaves** transpiles `Regenerate`, and because our Prefix
replaces the whole method its transpiled body never executes. That is not a load-order problem and
cannot be resolved by one — it is why §15 reimplements the feature natively and why `About.xml`
declares the mod `<incompatibleWith>` rather than merely `<loadAfter>`. The mod's other three patches
(`SectionLayer_IndoorMask.Regenerate`, `HideRainFogOverlay`, and the `MapMeshDirty` transpilers)
collide with nothing of ours — which is precisely the trap: with both installed the *rest* of Eaves
keeps working, so the breakage reads as "eave shadows randomly stopped" rather than as a conflict.
Also note `Patch_ShadowRoofInvalidation`, added by §15, Postfixes `SectionLayer_SunShadows`'s
constructor to widen `relevantChangeTypes`; it only ever ORs a flag in, so it composes with any
other mod doing the same.
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
No custom shader is written or shipped anywhere in this mod: the shadow work writes only into
vanilla's own mesh (per-vertex alpha) and vanilla's own material colour. §3 records the
`resources.assets` scan that ruled a shader-side approach out rather than leaving it untried.

## Per-frame geometry memo (`GeometryMemo` / `FrameStamp`)

**Problem.** Every subsystem above funnels through two adapters — `SolarPosition.InputsForMap` and
`MoonPosition.SkyForMap` — and that funnel is deliberate: it is what stops two patches deriving
slightly different suns (§1/§5) or moons (§6a). The cost is that both get called many times per
frame with identical arguments, because three vanilla facts multiply the fan-out:

- `Game.UpdatePlay` calls `MapUpdate()` on **every** loaded map, not only the current one.
  `SkyManager.SkyManagerUpdate` gates its material/shader writes on `map == Find.CurrentMap`, but
  `curSky = CurrentSkyTarget()` sits outside that gate.
- `CurrentSkyTarget()` evaluates `WeatherWorker.CurSkyTarget` **twice** (current weather and last
  weather) and lerps.
- `GenCelestial.CurShadowStrength` runs twice per `SkyManagerUpdate` — once directly, once inside
  `SetSunShadowVector`.

Counted per map per frame, before the memo:

| path | `InputsForMap` | `SkyForMap` |
|---|---|---|
| `CurSkyTarget` postfixes (§2, §8, §7, §13, §6a) × 2 weather workers | 14 | 2 |
| `CurShadowStrength` → `Patch_ShadowStrength` × 2 (current map only) | 6 | 2 |
| `SetSunShadowVector` → `Patch_ShadowDirection` (current map only) | 3 | 1 |
| `DateReadout` HUD (current map only) | 1 | 1 |

The per-call work is not just trigonometry: `Find.WorldGrid.LongLatOf` → `PlanetLayer.GetTileCenter`
is a managed→native `[BurstCompile]` transition, and `MoonPosition` paid for a second one on top of
the one inside `InputsForMap`.

**Approach.** A one-entry-per-map memo on exactly those two entry points, keyed on a
`GeometryStamp` of `(frame, tick, variant, scalar)`. Both collapse to **1 evaluation per map per
frame**. Nothing else is memoized — everything downstream already routes through these two, and
`ElevationForMap` is a single trig call over the memoized `Inputs`.

Why all four key fields, given both functions are "pure in `(map, tick)`":

- **frame** (`Time.frameCount`) bounds staleness to one frame, so any input not named below
  self-heals next frame. A tick-only key would cache forever while the game is paused.
- **tick** (`TicksAbs`), because frame and tick are not locked together: at 3×/4× speed several
  ticks run inside one frame, and while paused none do.
- **variant**, packing the two dev-only warp flags (`SunClockAdapter.WarpEnabled`,
  `MoonPosition.WarpMoonClock`) and the §14 `SunClockMode` setting. These are the inputs that are
  neither map nor tick, and the live harness writes the warp flags mid-run.
- **scalar**, the moon's synodic cycle position (sun passes 0), because the harness's eclipse
  staging shifts the whole cycle by writing `GameComponent_MoonPhase.debugSynodicShiftTicks` —
  changing the moon without changing the tick.

The last two exist to answer a specific question rather than assume it away. `ScenarioDriver` runs
exactly one step per frame from a `Root_Play.Update` postfix, so a probe always reads on a **later**
frame than the `SetFeature` step that moved a flag, and a frame-keyed memo alone would already be
correct today. Keying on the flag values themselves costs one `int` compare and stops that
correctness from being a property of the harness's step scheduler. The one live-mutable input we
cannot key on is the harness's own `Patch_ForcedLatitude` (it overrides `WorldGrid.LongLatOf`
results, and the shipped mod cannot see `HarnessRuntime`) — that one is covered by the frame field,
via the same one-step-per-frame guarantee.

`GeometryMemo.cs` is `System`-only and linked into the test project; `FrameStamp.cs` is the thin
Verse/Unity half that reads the frame, tick and flags — the same split as `SunClockMath` /
`SunClockAdapter`. `GeometryMemoTests.cs` covers hits, per-map isolation, invalidation on each key
field, and that a compute which throws caches nothing.

### What the memo cannot do: one evaluation is not the same as none

The table above is a fan-out count, and reading it as a cost table is the trap. The memo collapses
*calls* into *evaluations*; it has nothing to say about whether that one surviving evaluation should
have happened at all. For the moon in daylight, it should not — nothing on screen between sunrise
and sunset consults lunar geometry. §7's `Patch_NightRadiance` returns above
`NightFloorStartElevation`, and §6a's `Patch_MoonShadowColor` returns as soon as the sun clears the
refraction horizon.

One caller did not, and it sat on the daylight side of its own elevation gate:
`Patch_WeatherShadowColor` read `NightRadiance.FloorGlowFor(map)` unconditionally and fed it to
`ShadowFillMath.DaytimeUmbraFill`, which consumes it only in its `inVacuum` arm and returns the
plain sky fill otherwise. On every surface map — every ordinary colony — that value was computed and
discarded, and computing it ran the whole lunar simulation. So the mod carried a full moon
evaluation per map per frame across the entire lit half of the day to produce a number no branch
read.

Measured with `geometry_eval_count.json` (`GeometryEvalCountProbe`), surface map, latitude 45:

| | moon calls/frame | moon evals/frame |
|---|---|---|
| noon, before the gate | 2 | **1** |
| noon, after the gate | 0 | **0** |
| 01:00 (control), either build | 10 | 1 |

The night row is the control and does not move: this changes *when* the moon is asked, never what it
answers. Solar stays at 1 evaluation throughout, as the table above promises.

The fix is `inVacuum ? NightRadiance.FloorGlowFor(map) : 0f` — the same shape issue #64 applied to
`Patch_LimbRefraction`, whose own follow-up list named `Patch_WeatherShadowColor` as the remaining
instance. The scenario's three daylight blocks were reporting-only (`tolerance: 1000000`) and are
now pinned at `0 ± 0.5`, so a future daylight consumer of lunar geometry has to be a deliberate
decision rather than a silent one.

The general lesson, and the reason this sits next to the memo rather than in §16: a memo makes the
*repeat* cost of a question free and thereby hides the question. Rank callers by whether they need
the answer, not by how often they ask.

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

## Packaging and publishing (`publish.sh`)

**Problem.** RimWorld's in-game Workshop uploader does not take a file list.
`Verse.Steam.Workshop.SetWorkshopItemDataFrom` ends with
`SteamUGC.SetItemContent(handle, hook.Directory.FullName)` — the mod directory root, recursively,
no filter and no opt-out. This repo *is* that directory (the `Mods` entry is a symlink to it), so
an in-game upload publishes `Source/`, `Tests/`, `Tools/`, `TestMod/`, the ~150KB `DESIGN.md`, the
1.2MB `PreviewBig.png` (kept locally, never committed), `.git/`, and a `.pdb` whose portable-PDB
metadata carries absolute `/home/deck/Developer/...` build paths. Roughly 600KB of mod inside tens
of MB of scaffolding.

**Approach.** `publish.sh` stages a curated tree into `dist/CelestialLighting/` and points both
uploads at that tree rather than at the repo. Two rules decide what lands there. Scaffolding-free
essentials are named one by one — `About/About.xml`, `About/Preview.png`,
`About/PublishedFileId.txt`, `1.6/Assemblies/CelestialLighting.dll`, `LICENSE` — because nothing
about their path marks them as shippable. Loadable *content* is discovered instead: every tracked
file under a `Defs`/`Textures`/`Sounds`/`Patches`/`Languages`/`Sprites`/`AssetBundles` directory,
at the repo root or under a version directory, ships automatically. `LICENSE` is there for MIT's one
obligation: the notice has to accompany copies, and a subscriber's mod folder is their copy —
About.xml naming MIT is discoverability, not the notice. Steam gets it via a generated
`workshop.vdf` fed to `steamcmd +workshop_build_item`; GitHub gets the same tree zipped and
attached to a release by `gh release create`.

Content is discovered rather than listed because the listed version shipped v1.0.0 broken. A guard
was supposed to catch a forgotten manifest entry, but it tested `[ -d Defs ]` at the repo root while
our content lived at `1.6/Defs/`, so it matched nothing and passed vacuously. The release went out
with no `Defs/` at all, and every subscriber's log opened with `Failed to find
RimWorld.MapMeshFlagDef named CL_SunShadowAxis. There are 15 defs of this type loaded.` —
`MapMeshFlagDef`'s implicit `ulong` cast is `def?.mask ?? 0`, so the null DefOf threw nothing that
pointed at the cause. A whitelist you must remember to update is the wrong shape for content whose
only job is to be loaded.

Two properties of that split are worth stating, because both are easy to get wrong later:

- **The Workshop page survives a push untouched.** `SubmitItemUpdate` applies exactly the fields
  that were `Set` on the update handle, and the VDF sets only `contentfolder` and `previewfile`.
  Title, description and the `1.6` version tags that RimWorld's own uploader wrote are therefore
  preserved. The corollary is that this path can never *add* a version tag: when 1.7 support lands,
  the new tag needs one in-game upload or a manual edit on the Workshop page, or subscribers
  filtering by version will not see the mod.
- **Steam pushes stay local, GitHub releases could be automated.** The first `steamcmd +login` is
  interactive because of Steam Guard; afterwards credentials cache and the script runs unattended.
  Automating the Steam half from CI would mean committing a `config.vdf` session to a repo secret,
  which trades a real credential for very little — so `--steam` is a local operation by design.

Four guards, all covering failures that are silent at upload time:

- A version directory in the repo that `About.xml` does not declare (or vice versa) aborts the run.
  Shipping assemblies under an undeclared version means RimWorld never loads them; declaring a
  version with no assemblies means the mod loads and does nothing.
- Content sitting in one of those directories but never committed aborts the run. Discovery reads
  the tracked file list, so an uncommitted def or texture is invisible to staging and would ship as
  a missing one — far more often a forgotten `git add` than a deliberate exclusion.
- **A staged assembly that binds a `[DefOf]` field to a def the package does not contain aborts the
  run.** This is the other half of the v1.0.0 failure and the half discovery does not address:
  shipping `Defs/` by default stops the omission, but assembly and def tree can drift apart in the
  opposite direction too — delete the def, keep the field — with an identical symptom. Only the
  staged tree can be asked, and only from outside the game: the dev install is a symlink to the
  repo, so a running game always sees assembly and defs together and cannot reproduce a
  disagreement that exists solely in the package. `PackagedDefOfTests` reads the staged DLL's
  `[DefOf]` fields with Mono.Cecil, mirrors `DefOfHelper.BindDefsFor`'s rules (public static fields,
  `[DefAlias]` overrides the name, `[MayRequire…]` fields are allowed to be null), and resolves each
  against the staged `Defs/` plus RimWorld's own `Data/`. `publish.sh` points it at `dist/` through
  `CL_PACKAGE_ROOT`; run bare from `./test.sh` it checks the repo tree, which is the same question
  one step earlier.
- `--dry-run` writes `dist/workshop.vdf` and prints both upload commands without running them.
  Both uploads are hard to walk back; this is the intended way to inspect what would ship.

The guard was written against the failure still live on the Workshop: pointing it at the subscribed
copy of v1.0.0 reproduces `MapMeshFlagDef CL_SunShadowAxis`, and pointing it at a freshly staged
`dist/` passes. Both were confirmed in-game as well, by symlinking each package in turn as the
`Mods/CelestialLighting` entry and booting the `moon_illumination` scenario: the published copy logs
the error, the staged one logs nothing and passes.

## Interop: Realistic Axial Tilt (`Source/AxialTiltCompat.cs`)

**Problem.** Realistic Axial Tilt (RAT, `dsweber.RealisticAxialTilt`) lets the player choose their
planet's obliquity at world gen and reshapes the seasons around it. We render the sky. Left alone
the two mods fight over the same vanilla members: both postfix `GenCelestial.GetLightSourceInfo`
and `GenCelestial.CurShadowStrength` and both overwrite `__result`, neither declaring a Harmony
priority; and both prefix `SectionLayer_SunShadows.Regenerate` with `return false`, so whichever
registers second never builds its shadow mesh at all — silently, with the winner decided by load
order.

**Approach — split by concern, not by patch.** RAT owns the planet's solar geometry; we own every
pixel of lighting. Enforcing that by reflecting into their internals and neutralizing their patch
classes one at a time would mean re-auditing their patch list every release, so instead the
mechanism lives upstream: we contributed a public API to RAT
(`RealisticAxialTilt.Api.RealisticAxialTiltApi`) carrying the planet's geometry plus a lighting
claim. `AxialTiltCompat.ClaimLighting()` calls it once at init and *every* RAT lighting patch —
present and future — stands down behind a single guard they maintain. Their axial-tilt gameplay
(temperature, seasonal amplitude, plant rest and dormancy, world-params UI) is untouched.

**Why we read declination, not the tilt angle.** RAT's seasonal phase is not vanilla's: vanilla
`SunPositionUnmodified` and our `Formulas.DeclinationSign` both use `-cos(dayOfYear/60·2π)`, while
RAT uses `sin(...)` — a quarter-year offset in where the solstices land. Consuming
`SolarDeclinationDegrees(dayOfYear)` makes the phase *their* contract, so a change upstream
propagates with no edit here. Reading `AxialTiltDegrees` and re-applying our own curve would agree
at the equinoxes and drift in between, which takes a season of play to notice.

**The moon.** `MoonPosition` no longer calls `MoonMath.MoonDeclinationDegrees`; it resolves the
moon's declination through `AxialTiltCompat.MoonDeclinationDegrees`, which has two arms.

*With RAT's lunar geometry* (upstream `13e709e`), the moon is theirs: `LunarDeclinationDegrees`
places it on an **inclined** orbit — a player-tunable inclination with a regressing ascending node —
rather than on the ecliptic exactly. That is planet geometry, so it is theirs under the split. What
stays ours is the **phase**: we pass our own `cyclePosition` from `GameComponent_MoonPhase` into
their function, which their API explicitly invites ("supply your own cycle position … for any
offset"). Phase drives illumination, moonlight, the HUD label and eclipse staging — all lighting,
all ours. The consequence worth stating: with both mods installed, RAT's `moonOrbitalDays` slider
does not move our moon, because our cycle is the one overriding it. Their `moonInclinationDeg` does.

*Without it* — no RAT, a RAT predating their lunar block, or the feature switched off — we evaluate
the *sun's* declination function at `MoonMath.MoonEquivalentSunDayOfYear(dayOfYear, cyclePosition)`,
because the moon rides the same ecliptic and an offset in ecliptic angle is an offset in
day-of-year. The two arms are the *same model at two inclinations*, which is what makes the fallback
a baseline rather than a degradation: RAT builds the moon's ecliptic longitude as
`(dayOfYear/60 + cyclePosition)·2π` — the sun's longitude advanced by the elongation — which is
precisely that shifted day, so setting their inclination to 0 collapses the arms onto each other.
`MoonMathTests.MoonEquivalentSunDayOfYear_MatchesRatEclipticLongitudeConstruction` pins our side of
that identity.

Either arm keeps the moon on whatever seasonal model the sun is on, which is the point: rebuilding the
moon from our own `-cos` while the sun ran on RAT's `sin` would leave the two bodies a season
apart, visible only as a moon riding high on the wrong nights months into a save.
`MoonMathTests.MoonEquivalentSunDayOfYear_ReproducesMoonDeclination_ThroughTheSunModel` pins that
the indirection is exactly inert when the sun model is our own.

**Why the lunar arm is behind a feature flag** (`axial_tilt_lunar_geometry`, default on) when the
solar seam is not. RAT's lunar block arrived *additively*, and by their own contract additive
changes leave `ApiVersion` alone — so the version gate cannot see it, and two RAT builds a player
might have answer `GeometryReady` identically while only one has a moon.
`AxialTiltCompat.LunarGeometryActive` is therefore the flag **and** `Active` **and** a resolved
`LunarDeclinationDegrees`, never one of the three. Turning it off does not disable the interop —
under RAT the moon still runs on their seasonal model, just without their orbital inclination — so
"off" is simultaneously the harness's A/B baseline and a real escape hatch if an upstream lunar
change ever misbehaves.

**Measured.** One live run at latitude 45, day-of-year 15, noon (`axial_tilt_interop`, which flips
the feature mid-scenario so both arms see the same world, tick and moon phase): moon declination
**21.85°** on their inclined moon, **22.91°** on the fallback, against a sun that reads 23.45° in
both arms — the flag reaches the moon and nothing else. Without RAT the same moon reads **4.87°**
(`axial_tilt_absent`), a quarter-year away: that gap is the seasonal-phase handover, not the
inclination. All three match an offline recomputation of RAT's `LunarSinDecl` to four decimals.

**One seam.** Both reads funnel through `AxialTiltCompat.SolarDeclinationDegrees`, consumed at
`SolarPosition.ComputeInputsForMap` (sun) and `MoonPosition` (moon). Because every sun-derived
effect in the mod already resolves through `SolarPosition.Inputs`, that single line re-bases
shadows, twilight, penumbra, night radiance and moon shadows together. Without RAT it is literally
`Formulas.SolarDeclinationDegrees(dayOfYear)`, so the seam is inert for the single-mod case.

**Conflict risk.** No hard assembly reference — everything is late-bound via `AccessTools`, the
same idiom RAT's own `Compat/` classes use, so a user without RAT loads a build that has never
heard of it. There is deliberately **no** fallback path into RAT's internals: an older RAT without
the API is treated as absent (`ApiVersion >= 1` gate), rather than half-supported. Upstream merged
the API and the lighting gate (`08f5b49`; all seven of their lighting patches consult
`LightingSuppressed`) and has now published it, so RAT sits in `<loadAfter>` rather than
`<incompatibleWith>`: loading after them means their world comp has seeded its geometry before we
bind, and `ClaimLighting()` stands their rendering patches down at init.

What that tag used to guard against still describes a mod list running a **pre-API RAT build**: both
mods postfix `GenCelestial.GetLightSourceInfo` and `CurShadowStrength` overwriting `__result`, and
both prefix `SectionLayer_SunShadows.Regenerate` with `return false`, so whichever registers second
never builds its shadow mesh at all — silently, decided by Harmony registration order rather than by
anything the player can see. The `ApiVersion >= 1` gate is what still covers that case, and it
covers it in the only way that degrades safely: we treat that RAT as absent and render from our own
geometry, one renderer on a possibly mis-phased sun rather than two renderers on a coin flip. The
tag was the user-facing half of the same rule and is no longer the right instrument, because it
would now also reject the builds that compose correctly.

`GeometryReady` is checked on every read because RAT's `cosTilt` defaults to
`0f`, not `1f` — calling before their world comp seeds it returns a degenerate planet, not
Earth-like defaults. `GeometryGeneration` is exposed for cache invalidation across saves with
different tilts.

**Drift is a runtime failure, so it degrades rather than throws.** Late binding means every member
is a string: a rename or a signature change upstream cannot fail at compile time, and `ApiVersion`
is no defence because it moves only when *they* judge a change breaking — a rename they consider a
tidy-up is exactly the case they would not flag. So each resolve is null-checked and each
`CreateDelegate` is guarded, and any failure logs one warning naming the member and treats RAT as
absent. Before this, a rename propagated an exception out of a `StaticConstructorOnStartup` and took
the whole mod with it.

Two details are deliberate. The warning states the *consequence* rather than the fault, and it
differs per arm — losing a required member costs the planet's obliquity (our tilt, so seasons may
read out of step with RAT's own temperature and growing periods), losing `LunarDeclinationDegrees`
costs only an inclination. And `TryClaimLighting` is bound first, so if the geometry is unreadable
we still claim the lighting: declining would not hand the sky back to RAT, it would put both mods on
`SectionLayer_SunShadows.Regenerate` with a `return false` prefix each, where the loser silently
never builds a mesh and Harmony registration order picks it. One renderer with a mis-phased sun
beats two renderers and a coin flip.

Verified live against a RAT built with `SolarDeclinationDegrees` deliberately renamed:
`axial_tilt_absent`'s pins pass unchanged with RAT installed and active (declination 0, moon 4.87°),
RAT logs `Lighting claimed by joof.celestiallighting`, and the run carries exactly one warning and
zero exceptions — a drifted API is indistinguishable from RAT not being installed, which is the
goal.

## Interop: Planetsmith (`Source/PlanetsmithCompat.cs`)

**Problem — a disagreement, not a conflict.** Planetsmith (`aspctt.planetsmith`) is a
world-generation overhaul: a climate simulation and a competitive biome-scoring pass that replace
vanilla's biome placement. One of its world parameters is an axial tilt, a 0–90° slider defaulting to
23.4, which it spends deciding how hard the seasons shape the planet it is about to build — pole
temperatures, seasonality, where tundra gives way to boreal forest. It is not RAT: it patches nothing
we patch (its three Harmony targets are `Page_CreateWorldParams.DoWindowContents`/`PreOpen` and
`WorldGenStep_Terrain.GenerateFresh`), it renders no sun, and its assembly contains no reference to
any celestial or sky type. Nothing breaks with both installed.

What was wrong was quieter. A player who generates a world at 60° gets biomes laid out for a planet
with savage seasons, and then we light it with Earth's 23.44° — because that was the only number we
had. The map says one planet and the sky says another, and nothing anywhere reports it.

**Approach — take their obliquity, keep our phase.** `PlanetsmithCompat` reads
`PlanetsmithWorldComponent.Settings.axialTilt` off the loaded world by reflection and feeds it to
`Formulas.SolarDeclinationDegrees(dayOfYear, obliquityDegrees)`, a new overload that scales our
existing `-cos` phase term by a tilt someone else chose. Because every sun-derived effect already
resolves through `SolarPosition.Inputs`, one number re-bases shadows, twilight, penumbra, night
radiance and the moon together.

**Why this reads the tilt angle where the RAT interop deliberately does not.** The section above
argues at length that consuming RAT's obliquity and re-applying our own curve would be wrong, and it
would — for RAT. RAT reckons the year from a different point than we do (`sin` where we use `-cos`, a
quarter-year apart), so their tilt without their phase agrees at the equinoxes and is a season out in
between. Planetsmith has no phase to disagree about, and this is worth stating from their code rather
than from their description, because "does it have a seasonal curve we should be using instead?" is
the first question this design has to answer. It does not. Their tilt does exactly three things:

    TiltFactor    = AxialTilt / 23.4f                                  // a scalar ratio
    PoleMeanTemp += (AxialTilt - 23.4f) * 0.6f                         // a constant offset
    swing[i]      = Lerp(3, 22, |lat|/90) * max(0.12f, TiltFactor)
                      * seasonIntensity * (1 + 0.6f * continentality)  // an amplitude

`SeasonalityPass` writes that swing out as `WinterMinTemp[i]` / `SummerMaxTemp[i]` — two *bounds*,
not a curve — and those arrays are read only by `BiomePass.SelectBiome`, `MonsoonPass`, and their
world-map overlay. There is no day-of-year term anywhere in the assembly, and nothing they compute
reaches the running game's temperature, let alone a sun position. Their own world-params dialog says
the same thing in prose, warning that this tilt "decides how the seasons shape the planet's biomes
while it is generated" and is separate from the one Worldbuilder uses for in-game swings. So scaling
our curve by their obliquity is the whole of the correct answer here, not a shortcut around a
missing API — there is no curve of theirs being ignored.

**The units agree by construction.** Their tilt enters linearly (`TiltFactor = tilt / 23.4`) and so
does ours (`declination = tilt × -cos(...)`), so a 60° world is 2.56× the seasonal temperature swing
for them and 2.56× the declination amplitude for us. The sky's seasons stay in proportion to the
biomes' without any tuning constant of ours to keep in step with theirs.
`FormulasObliquityTests.Obliquity_EntersLinearly` pins our half of that.

**One deliberate divergence,** at the upright end of the slider. Their `MinTiltFactor = 0.12` floor
keeps 12% of a baseline swing even at tilt 0, while our declination there is exactly zero — a
genuinely seasonless sky over biomes that still carry a slight seasonal spread. The floor exists so
their biome scoring does not degenerate into a single band, not because an upright planet has
seasons, and mimicking it would mean tilting our sun on a world the player asked to stand upright.
Pinned and named in the tests so it reads as a decision rather than an oversight. `FormulasObliquityTests.Obliquity_ScalesTheSwingWithoutMovingThePhase`
pins the property that makes it true — solstices and equinoxes land on the same days at every tilt.

**Precedence.** With both third-party mods installed RAT wins and Planetsmith is not consulted;
`AxialTiltCompat.SolarDeclinationDegrees`' else-arm is the chain, RAT → Planetsmith → our constant.
The ruling is that RAT is simulating the running year and owns phase, tilt and moon together, while
Planetsmith's tilt was spent at generation and is by then a record of how the map was built rather
than a claim about the sky. Two mods cannot both define the obliquity; believe the one still
simulating. `planetsmith_active` still reads 1 in that case — the interop is live, it just lost — and
`planetsmith_tilt` reports RAT's number, which is how a scenario would catch that precedence
silently inverting.

**Which tilt, exactly.** The world component's copy, not `PlanetsmithMod.Settings.axialTilt`. The
latter is what the NEXT world will be generated with and moves whenever the player opens the settings
screen; the former is what THIS planet was built for, saved beside it, and the only one that
describes the biomes now on the map.

**One seam that had been leaking.** `Patch_SunGlow` (§14's opt-in realistic sun-clock mode) called
`Formulas.SolarDeclinationDegrees` directly rather than the seam, so with any geometry provider
installed it lit the sky on Earth's tilt while the shadows ran on the planet's. Routed through
`AxialTiltCompat.SolarDeclinationDegrees` here. That was a live inconsistency for RAT too, not
something this interop introduced — it just made it reachable a second way.

**Conflict risk.** No hard assembly reference; every member is a string resolved at runtime, so a
player without Planetsmith loads a build that has never heard of it. Unlike RAT there is no
negotiated API — these are their internal field and property names, a weaker contract, treated as
one: every resolve is null-checked, a miss logs once naming the consequence rather than the fault,
and the read is wrapped because it walks two hops into another assembly's object graph on the
per-frame geometry path, where a throw would be one error per frame forever. NaN is rejected twice
over (at the read, and again in `Formulas.SanitizeObliquityDegrees`) because a NaN tilt does not
throw — it propagates through every trig call downstream and lands as an invisible sun and a
shadowless noon, which reads to a player as our bug.

What is cached is the component lookup, not the tilt: the lookup is the expensive part (a walk of
`World.components`) and the part that genuinely cannot change while one world is loaded, while the
field is re-read every call so a mid-run change is seen and no second copy of the number can go stale
against a reload. That is not a theoretical preference: two live runs failed against a build that
still cached the value, with the override writing 60 and the probe reading back the cached 23.4 — a
symptom indistinguishable from the interop simply not working. The cache key is a
`System.WeakReference<World>` so it cannot keep a discarded
world's map graph alive after the player returns to the main menu.

**No opt-out, and that is the point.** `planetsmith_geometry` exists as a harness flag so a scenario
can reach both arms in one run, but nothing in a shipped game writes it and the settings screen offers
no switch. When a world-geometry mod is installed, the planet's obliquity is ITS setting; a control of
ours beside it would be a second source of truth for a single number, which is precisely the
biome/sky disagreement this interop exists to remove. A player who wants a different tilt changes it
where it is defined.

This is also what makes Planetsmith consistent with RAT rather than the exception. RAT's solar
declination has never had an opt-out — `AxialTiltCompat.SolarDeclinationDegrees` consults it whenever
`Active`, with no flag in the path — and an earlier draft of this interop gave Planetsmith one, which
would have left it as the single geometry source the player was invited to second-guess.
(`axial_tilt_lunar_geometry` is not a counterexample: it selects between two of RAT's own models for
the MOON, both of them theirs, rather than offering to overrule their planet.)

What the settings screen does instead is report: with either mod installed it shows one read-only
line naming the obliquity in force and which mod set it. It reads `AxialTiltCompat.ObliquityDegrees`
rather than either mod's field, so it displays the value actually in use and therefore *shows* the
RAT-wins precedence instead of restating it — with both installed the line names RAT.

**Testing.** The pure half is `FormulasObliquityTests` — 25 offline cases covering the scale/phase
split, the fixed equinoxes and solstices, periodicity and boundedness at every tilt, the sanitizer's
clamp and its NaN fallback, and four hand-computed noon elevations that state what a steep world
costs (at 45°N, midwinter noon goes from a sun 21.6° up on our tilt to one 15° *below* the horizon at
60°). The live half is `Tests/Scenarios/planetsmith_tilt.json`.

That scenario needs a world with a non-default tilt, and no save fixture can supply one: the tilt is
chosen in Planetsmith's world-gen UI and frozen into the save, and `minimal_colony.rws` predates
Planetsmith, so its component is constructed at load from their 23.4 default — 0.04° from our own
23.44, a world where the interop is live and completely invisible. `PlanetsmithTiltOverride`
(dev-only, under `Source/Probes/`) bridges a `planetsmith_steep_tilt` feature flag that writes 60°
into the loaded world and restores the original on the way out, which is also the only way to A/B two
tilts against one world. 60 rather than 90 deliberately: 90 is the end of the slider, so a clamp bug
that pinned every tilt to the maximum would pass.

**Their tilt, our clock — and why that split is forced rather than chosen.** Planetsmith publishes no
day length. It patches no day-length member (its only Harmony targets are `Page_CreateWorldParams`
and `WorldGenStep_Terrain`) and its assembly contains no reference to `CelestialSunGlowPercent`,
`SunPosition`, `DayPercent`, `GenCelestial`, `TwelfthOfYear` or `SeasonalShiftAmplitude` at all. So
there is no day length of theirs to adopt even if we wanted one.

This is the sharp asymmetry with RAT, and it is worth holding next to the section above. RAT patches
`GenCelestial.CelestialSunGlowPercent` (`GlowCurvePatch`) along with `SunPosition`,
`CurSunPositionInWorldSpace` and `GenTemperature.SeasonalShiftAmplitudeAt` — it re-tilts *vanilla's
own* glow curve, so when §14's default `LockedToVanilla` mode snaps our sun to vanilla's clock, it is
snapping to a clock that already carries the planet's obliquity. Locked mode is not a compromise
there; it is exactly right. With Planetsmith nothing re-tilts that curve, so the same snap lands on
Earth's day length over a planet built for another tilt.

We take the tilt and keep the clock anyway, deliberately. Locked mode's standing bargain is already
"faithful sun altitude, vanilla day length" — that is what the warp *is*, and §14 documents the trade.
Planetsmith's obliquity lands squarely on the axis locked mode reproduces honestly and does not touch
the one it was always going to fake, so it makes the sky agree with the biomes where the sky is most
visible without introducing a new kind of error. Gating the interop on the realistic sun clock was
considered and rejected: it would make the setting silently inert for everyone who never leaves the
default, which is worse than a partial effect that is real.

**How much reaches the screen, measured.** The declination handover is total — 23.4° at Planetsmith's
default, 60° with the override on, 23.44° with the feature off, −60° at midwinter, each within 0.05°.
On screen the effect is FULL AT NOON and tapers toward sunrise and sunset, because `WarpDayPercent`
works in offset-from-noon space (`d = dayPercent - 0.5`) and therefore maps noon to noon exactly,
rescaling only the distance either side of it. At 45°N on day 30:

| clock hour | 23.4° world | 60° world | shadow change |
|---|---|---|---|
| 12:00 | 68.40°, 0.554 cells | 75.00°, 0.375 cells | −32% |
| 15:00 | 52.69°, 1.067 cells | 54.39°, 1.003 cells | −6% |

Both elevations at noon are the analytic `90 - |lat - decl|` to the pin's tolerance, and the measured
shadow ratio 0.6767 matches the cotangent ratio 0.6768 to four places — the geometry arrives intact,
it is only the day's *pacing* that vanilla still owns. All four rows are pinned; an earlier draft of
this section quoted the 15:00 figure alone and understated the effect fivefold, which is why the noon
row is now the one the scenario leads with.
