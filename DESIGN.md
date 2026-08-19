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

**Snow lifts the floor (§21).** `NightRadiance.FloorGlowFor` multiplies the assembled floor by
`SurfaceBuildup.CavityGainFor(map)` — the surface-cloud light cavity. Snow on the ground and a cloud
base overhead trap light between them, amplifying starlight and moonlight by up to 2.34×, so a
snowed-in map under an overcast no longer goes as black as a bare one. That is a real tension with
this subsystem's pitch-black-nights premise and §21 resolves it in favour of the physics; it is
resolved by the map's own state (mean buildup depth × §13's cloud opacity) rather than by a special
case, and the gain is exactly 1 — bit-identical to pre-§21 — on any map with no buildup. See §21.

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
    cell past it onto open ground. `holdsRoof` excludes it, exactly as vanilla does — *unless* the cell is
    unmined stone (`isThickRoof && building.isNaturalRock`), which is buried because there is no wall face
    there at all.
    That exception used to read `isThickRoof` alone, i.e. a mountain buried whatever was under it, wall or
    not, borrowed from vanilla's `isThickRoof` disjunct. It was over-applied on two counts (#129): the
    disjunct is in vanilla's **corner** pass only — its centre pass tests a bare `Roofed && !holdsRoof`
    with no thickness term — and even there it raises the cover to the 100 floor rather than blacking the
    cell out. Reproducing a roof-*support* rule as "this cell's own sky reads zero" swallowed the entire
    wall ring of a mountain room into the same black square as its floor: no wall texture, no boundary, no
    ramp. Narrowing to natural rock keeps the half that was right (unmined stone genuinely has no sky) and
    drops the half that was not (a built wall under a mined-out mountain roof is the same wall it would be
    under a constructed one). It is the same one-predicate-was-two-questions split `EavesMath` already made
    against this same `thickRoof` veto. Note an interior partition wall deep in a mountain base is still
    fully occluded — but by its own four corners, every one shared with interior floor, which is the
    correct reason rather than the roof over it.
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
- **Doors are boundary cells, not interior.** Vanilla lumps doors in with roof for cover
  (`altitudeLayer == AltitudeLayer.DoorMoveable` is one of the disjuncts that sets its corner flag) and
  a closed door's `blockLight` suppresses glow too, so at full occlusion a doorway would go dead black.
  A door is instead treated exactly like open ground — never interior, so it can never propagate
  darkness outward through the wall line. This used to also carry a flat `DoorSkyLeak` cap (default
  0.15) on the corners a door touched, brightening the threshold a shade above the wall either side of
  it. Removed once §7c's native sky falloff shipped: that gradient already reaches door-adjacent
  corners through `CapOcclusion`'s `skyFalloffFraction` term, scaling with distance from the opening
  rather than a flat amount, so the fixed cap only ever duplicated or clashed with it. See §7c below.

  One trade-off worth being explicit about: a player who has both §7c's native falloff *and* Ambient
  Light turned off (or not installed) now gets a doorway that reads identically to a plain wall corner —
  there is no fallback brightening left to fill that gap. That is the accepted cost of not maintaining
  two independently-tuned brightening terms for the same visual effect.
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
- **A second, independent floor for whatever else is lighting interiors (`IndoorGlowPassthrough`,
  issue #80).** §7b decides occlusion from geometry alone, so any mod that redistributes sky glow into
  roofed cells produced a real, distance-graded, mouseover-reportable *gameplay* value that rendered
  flat black: an interior cell could read 46% lit by Ambient Light's own accounting while
  `CapOcclusion`'s only floor was `minIndoorBrightness`. It now takes a second, independent floor and
  applies whichever of the two is looser (`Min` of both `1 - floor` ceilings, so they compose rather
  than one silently overriding the other).

  **The signal is vanilla's own, and names no mod.** `GlowGrid.GroundGlowAt` gates its sky term on
  `!map.roofGrid.Roofed(c)`, so on a *roofed* cell vanilla returns nothing but the artificial term —
  therefore **anything above the artificial glow was put there by somebody else, and is sky-sourced by
  elimination.** Identically 0 on an unmodded install, so it cannot move a vertex by itself.

  Two details are load-bearing. The artificial share is **recomputed** from `VisualGlowAt` using
  vanilla's own formula rather than obtained as `GroundGlowAt(c, ignoreSky: true)`: Ambient Light's
  postfix is declared `Postfix(ref float __result, GlowGrid __instance, IntVec3 c)` — no `ignoreSky`
  parameter — so it fires on both calls and that difference is identically zero (verified by
  decompiling `AmbientLightFalloff.Patch_GlowForcedGround`). And the lamps are **subtracted** rather
  than the total being used, because this value caps how much of the *sky's* colour a vertex shows;
  capping on total glow would put dawn pink on a windowless, well-lit workshop.

  **This replaced `AmbientLightCompat`**, which bound by reflection to one named mod — its map
  component, its settings object, its `GetDepth` — and re-derived its private falloff formula
  byte-for-byte. That worked and was the wrong shape: it fixed exactly one mod, broke whenever that mod
  refactored, and every further mod would have needed its own copy. Measured equivalence: the general
  term recovers **the same 0.4583** the reflection produced, with no reflection at all.

  **It also reaches a case the old arm structurally could not.** ReBuild: Doors and Corners
  (`ReBuild.COTR.DoorsAndCorners`) transpiles `GroundGlowAt`, swapping `roofGrid.Roofed(c)` for its own
  `HasNoNaturalLight()`, false for cells in its `cellsNearbyGlassWalls` set — so cells near a **glass
  wall** receive `CurSkyGlow` as real gameplay light, graded by its own BFS. Its 1.5 assembly also
  patched `SectionLayer_LightingOverlay.Regenerate` to render that; **1.6 dropped that patch** and
  relies on vanilla's overlay, which §7b was painting to full occlusion — darker than vanilla's own
  `RoofedAreaMinSkyCover == 100`. §7c's native BFS cannot help here: a glass wall holds roof and is not
  a door, so it blocks the flood outright. The passthrough fixes it with no glass-specific code, and
  the room lights following *their* grading. Live: `Tests/Scenarios/glass_wall_leak.json`, median
  CIELAB ΔE **22.35** over changed pixels (full-frame median 0.00 — the room is 3% of the frame; the
  far-field control moved 0.52%).

  **A bespoke transparent-wall leak was built for this and deleted.** It classified an edifice from
  `blockLight == false` (gated on `holdsRoof || isDoor`, so furniture could not punch holes) and capped
  the corners it touched. It worked in the pure core and measured **completely inert on screen**: with
  ReBuild loaded, vertex alphas were identical with it on and off, because the passthrough had already
  capped those cells to 0. Recorded because "add a per-mod special case" is the tempting answer and
  this is the evidence the general one subsumes it. Shadows needed nothing either way: ReBuild's glass
  declares `staticSunShadowHeight` 0, which `Patch_ShadowMeshPerimeter` and `EaveShadowGrid` already
  read.

  **Deferral is whole-map, not per cell.** When another mod *owns* under-roof falloff — supplies a
  whole-map distance-from-an-opening gradient of its own, as Ambient Light does — §7c's native BFS is
  not consulted anywhere on the map, and is never even built. Falling through per cell is the easy
  mistake and reintroduces exactly the seam §7c set out to avoid: a cell just past their `maxDepth`
  returns 0 from the passthrough and would then be answered by our gradient, which has an
  independently tuned `maxDepth`, so a discontinuity appears *inside a single room* that neither
  gradient has on its own. `SkyFalloffArbitration` is the pure rule; `UnderRoofFalloffOwner` is the
  detection.

  That detection **names mods**, unlike the passthrough, and the reason is concrete. The tempting
  general test — "has anyone other than us patched `GroundGlowAt`?" — is wrong: ReBuild patches
  exactly that method, but only to pass light through its glass walls, and supplies no door gradient
  at all. Standing §7c down for it would silently delete under-roof falloff for every player who has
  ReBuild. "Does this mod own the whole gradient" is not observable at runtime, so it is a short
  explicit list with the reason recorded per entry. Measured both ways: with Ambient Light installed
  the near-door cell reads `ambient_sky_fraction` **0 → 0.4583** across the passthrough toggle (0, not
  §7c's 0.2625 — the native gradient really is out); without it, `native_sky_falloff.json` still reads
  depth 2 and **0.2625** at that same cell, so the door leak is untouched.

  **The lamp guard.** `Tests/Scenarios/indoor_glow_lamp.json` pins the subtraction: a sealed, roofed,
  torch-lit 25×25 room reads ground 0.5, artificial 0.5, **sky 0**, identical at noon and midnight,
  with and without Ambient Light loaded. `TorchLamp` rather than `StandingLamp` — the latter needs a
  powered conduit, sits dark, and would make every probe read 0 while the scenario passed.
- **Baked, not per-frame.** Unlike §7a's material colour, these alphas only change when a section is
  dirtied, so `IndoorOcclusionRedraw` forces a `WholeMapChanged(GroundGlow)` when the toggle or either
  slider changes (it compares the *resolved* floor, so either knob moving is caught without duplicating
  the max() rule) — otherwise the setting appears to do nothing until something else dirties the map.
- **The same staleness applies to time itself, and is fixed the same way (`GameComponent_SkyFalloffRedraw`).**
  `SkyFalloffSource.FractionAt` (both its native-BFS and passthrough arms) is a function of
  `CurSkyGlow`, but nothing about the clock advancing dirties a section — only a roof edit or a glow
  change does. Left alone, a room baked at noon stays noon-bright straight through to midnight until
  something unrelated (a lamp toggling nearby) happens to rebuild it. Fixed with a `GameComponent`
  (never a `MapComponent` — see the repo's own tombstoned `MapComponent_SunShadowAxis`, keyed on this
  exact same live/redraw shape and killed for it) that checks every 250 ticks whether each map's current
  `CurSkyGlow` has drifted ≥`SkyFalloffRedrawMath.DefaultThreshold` (0.05) from what its meshes were
  last baked against, tracked per map by tile ID (mirroring `CloudCoverClock.Cache`'s own reasoning for
  why a `Dictionary<int, float>` never cleared on despawn is safe) and calling
  `IndoorOcclusionRedraw.ForceRebuildMap` only for the maps that actually moved. **Why a drift gate is
  safe here despite the tombstone's warning against "dirtying sections on a clock, forever":** the
  tombstoned feature's driving value (sun azimuth) sweeps continuously all day, so any nonzero threshold
  still fired on a bounded schedule every single day. `CurSkyGlow` does not share that shape —
  `SunClockMath.GlowFromElevation` (and the vanilla `WeatherWorker.CurSkyTarget` curve it follows) holds
  glow flat at 0 through the night and flat at 1 through the day, moving only across the two civil-
  twilight ramps, a bounded fraction of the day — so the gate only ever fires during a dawn or dusk
  transition that is genuinely in progress, not forever. Skipped entirely (no map read at all) when
  neither `NativeSkyFalloff` nor `IndoorGlowPassthrough` is on, since `FractionAt` returns a flat 0 in
  that case and there is nothing to go stale. Gated by `CelestialLightingFeatures.SkyFalloffRedraw`,
  harness-only (no settings-screen toggle — "the mesh matches the current sky" is a correctness
  property, not a taste knob), so a scenario can hold the fix off, jump the clock across a civil-
  twilight ramp with no lamp or roof touched, and capture the stale mesh the bug used to leave behind.
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

### 7c. Native under-roof sky falloff — no Ambient Light dependency (`NativeSkyFalloffGrid` /
`SkyFalloffSource`, issue #124)

**Problem.** `IndoorGlowPassthrough` above (issue #80) only helps a player who has some interior-brightening
workshop mod installed — everyone else still gets §7b's blanket verdict, "every interior cell is fully
occluded", with no gradient near a doorway or a window gap. Issue #124 asked for the same
distance-from-opening falloff built natively, without a third-party dependency, and deliberately left
open how: whether to reuse the per-section window pattern `SkyOcclusionWindow`/`EaveShadowGrid` already
establish, and how to avoid two competing falloffs when Ambient Light is also present.

**Why not the per-section window pattern.** `SkyOcclusionWindow` and `EaveShadowGrid` work because the
thing they resolve per cell — is this roofed, what height does this cell cast a shadow at — only ever
depends on that cell and its immediate neighbours, so a one-cell skirt around the section is enough.
Distance-to-nearest-opening is not local in that way: a cell fifteen tiles into a mine has no correct
answer without seeing fifteen tiles in every direction, so a per-section approach would need a skirt
sized to `maxDepth`, re-run on every section regenerate — and regenerate fires on *any*
`GroundGlow`-dirtying event, including an ordinary lamp toggle (§7b's own header above already flags
this cost for a *cheaper* per-cell test than a flood fill). Re-running a `maxDepth`-radius flood on every
lamp flip is the wrong shape.

**Approach.** A whole-map multi-source BFS (`NativeSkyFalloffGrid`), run once per map and cached until
something changes, mirroring how Ambient Light's own `MapComp_AmbientLight.RebuildDistance` solves the
identical problem (decompiled in full to confirm): seed every unroofed cell at depth 0
(`!map.roofGrid.Roofed(cell)`, matching Ambient Light's own seed condition exactly), flood outward up to
`maxDepth`, 8-directional with a corner-cut guard so the flood cannot cut diagonally past a solid wall
corner no pawn could pass through either. A wall (`holdsRoof`, not a door) is never seeded and never
flooded into — it blocks the expansion outright — while a door is explicitly excluded from that wall
test, so the flood still crosses an open threshold and picks up depth from the genuinely unroofed cell on
the far side of it. One deliberate simplification against Ambient Light's own version: integer BFS-layer
depth rather than a diagonal-weighted Euclidean float distance — this mod's own gradient, not required to
match theirs bit-for-bit (see `NativeSkyFalloffMath`'s header for why the two formulas are independent by
design).

*Shipped once with a bug here, caught by a player report rather than by the test suite.* The first cut of
`Rebuild` seeded on `!IndoorOcclusionMath.BlocksSky(cell)` instead — the same "is this cell interior"
classification `Patch_IndoorSkyOcclusion.ResolveCell` uses for the rendering pass, reused on the theory
that keeping the two questions ("should this be painted dark", "is this cell a BFS opening") answered by
one predicate meant they could never disagree. They are not the same question: `BlocksSky` deliberately
returns `false` for a WALL cell too (a wall gets the corner-ramp treatment, not a flat fill — see
`BlocksSky`'s own header above), so seeding on it treated every wall around a room as if it were an
opening. A typical roofed room is only a handful of tiles from *some* wall on every side, so the whole
room read a shallow, near-uniform depth instead of a gradient concentrated near the actual door — visibly
a flat fill with the feature on, not a falloff, though every offline `[TestCase]` and the shipped probe's
huge tolerance both passed regardless, because nothing in that coverage compared one cell's depth against
another's. Fixed by splitting the two questions apart: seed on `!Roofed()` alone (Ambient Light's own
literal condition), and add a dedicated `IsWall` blocker check to the expansion loop so a wall neither
seeds nor gets flooded through in either direction — the latter matters on its own: without it, a wall
cell reached from one room's interior would still get enqueued and could hand its depth on to whatever
sits on the wall's *other* side, leaking one sealed room's falloff into its neighbour through solid
geometry. `CornerBlocked`'s diagonal-cut guard had the identical bug (it also read `BlocksSky`, so a
diagonal step could cut through a wall corner it should have refused) and took the same `IsWall` fix.

- **Not a `MapComponent`**, per this repo's own convention (parent `CLAUDE.md`): a single-slot
  `WeakReference<Map>` cache, so deleting the
  type later never scribes a permanent node onto every map.
- **Lazy, not ticked.** `MarkDirty` (called from `Patch_SkyFalloffDirty`'s three Harmony postfixes) only
  flips a bool; the BFS rebuild happens on the next `DepthAt` call that actually needs the answer. No
  `MapComponentTick` poll anywhere, unlike Ambient Light's own `_dirty` flag which a per-tick check
  consults whether or not anyone is currently reading the grid.
- **Invalidation mirrors `DirtyHooks`' validated trigger set** (decompiled from `AmbientLightFalloff.dll`
  this session) rather than inventing a new one: `RoofGrid.SetRoof`, plus spawn/despawn of anything the
  seed/blocker tests above actually read — `def.holdsRoof` (a wall) or
  `def.altitudeLayer == AltitudeLayer.DoorMoveable` (a door). Patched on `Building`, not `Thing` — a
  narrower target than Ambient Light's own Thing-wide patch, since `Building` overrides `SpawnSetup` and
  `DeSpawn` directly and every holdsRoof/door Thing is a `Building` by construction, so the narrower type
  still catches every relevant case while leaving an item drop or a pawn walking by untouched. `DeSpawn`
  uses a Prefix, not a Postfix, because `DeSpawn` clears the Thing's `Map` reference as part of what it
  does — the map has to be read before the call runs.
- **A `maxDepth` slider change is treated exactly like a dirty map.** The cached array is capped at
  whatever `maxDepth` was in effect when it was built; if a player raised the slider afterwards, cells
  the old cap marked "beyond reach" would silently stay wrong without this check.
- **Deferral, not composition, when Ambient Light is active (`SkyFalloffSource`).** The two sources use
  different `maxDepth` values and formulas by construction — players tune each independently — so
  `Max()`-ing them the way `CapOcclusion` already composes `MinIndoorBrightness` and
  `AmbientLightSkyFraction` would put a visible seam wherever the smaller `maxDepth` runs out, a
  discontinuity neither gradient has on its own. `SkyFalloffSource.FractionAt` is the single place that
  decides: `IndoorGlowPassthrough` wins outright wherever it answers (another mod's own mouseover readout already reports
  a gameplay-authoritative value, strictly better-grounded than a second guess computed independently),
  and the native BFS is never even run in that case — no wasted whole-map flood on every map that has
  Ambient Light installed. `Patch_IndoorSkyOcclusion`'s two call sites (the corner pass's MAX-over-four-
  neighbours and the centre pass) both go through this dispatcher now rather than calling
  `IndoorGlowPassthrough.SkyFractionAt` directly; `IndoorOcclusionMath.CapOcclusion`'s own signature and
  tests are untouched — only what feeds its third argument changed source.
- **Two sliders, added after live playtesting** — the original decision here was "no settings UI, matching
  Ambient Light's own precedent", with `passThroughPercent` fixed at 55f to land in the same
  register as Ambient Light's own shipped default. A playtester found that in practice: a typical roofed
  room read as generally lit up rather than gently graded near the door. The passthrough has no
  slider because it has no formula of its own to tune — it just relays another mod's number — but this
  subsystem's formula is ours, so unlike that precedent there is a real knob to expose. `MaxDepth` and
  `PassThroughPercent` are now `NativeSkyFalloffSettings.Current`, written by two sliders in
  `CelestialLightingSettingsMod` ("Sky brightness at an opening" 0-100%, "How far the glow reaches"
  1-24 cells), persisted like every other tunable. `NativeSkyFalloffMath.DefaultPassThroughPercent`
  itself also moved, from 55f to **25f** — the new out-of-box value reads as a gradient near an opening
  rather than a room light; `DefaultMaxDepth` stayed at 12, since the complaint was about brightness, not
  reach. Both changes are pinned: `NativeSkyFalloffMathTests.DefaultPassThroughPercent_...` catches a
  silent revert of the constant, and `IndoorOcclusionRedraw.SyncTo` — already the rebuild trigger for
  §7b's baked alpha — was extended to also watch these two fields, since `SkyFalloffSource` writes into
  the identical baked term; `NativeSkyFalloffGrid.EnsureCurrent`'s own existing dirty-on-maxDepth-change
  check (see above) already covered rebuilding the BFS cache itself, so only the mesh-redraw side needed
  the extra plumbing.

Gated by `CelestialLightingFeatures.NativeSkyFalloff`, default on — the whole point is to close the gap
for players without Ambient Light, so shipping it off would leave that gap unfixed by default. When off,
`SkyFalloffSource.FractionAt` returns 0 for every cell, identical to the passthrough's own off-state
and to `CapOcclusion`'s pre-feature identity — the faithful baseline for the harness A/B.

**Live-measured** at the same door-adjacent interior cell §7b's own writeup above uses, in a fresh
`Tests/Scenarios/native_sky_falloff.json` (identical 11×11 roofed room, run with Ambient Light *absent*
so the native path is exercised in isolation): the BFS depth at that cell is 2, giving fraction 0.4583 at
noon (`curSkyGlow` ≈ 1.0) against 0 with the feature off. That depth of 2 is worth calling out on its own:
it is the real distance from that cell, across the door, to the nearest genuinely unroofed cell outside —
and it matches `ambient_light_compat.json`'s own depth at the identical cell exactly (see below), which is
the cross-check that would have caught the seed-condition bug above before it shipped, had anyone thought
to compare the two paths' depth rather than only their final fractions. Cropped to the room interior,
median CIELAB ΔE between those two states is **8.95**, comfortably past the "visible at a glance" 5+
threshold — lower than the bug's own inflated 16.11 by design: that number averaged in a room-wide false
brightening the fix removed, while this one is the true median over a crop where only the cells actually
near the door move and the far wall correctly stays near-black. The near-door strip alone (excluding the
darker far wall) reads median ΔE 17.9 against the feature-off baseline, and the "on" frame's own near-door
strip differs from its own far-wall strip by median ΔE 10.1 — the gradient the whole-room number cannot
show by itself. A whole-frame median is 0.0 because the room occupies a small fraction of the captured
frame, the same scale mismatch that motivates cropping rather than a wider baseline. At 23:00 the same
toggle reads fraction 0.0183 against 0, and the interior crop's median ΔE is 0.0: not a regression, the
same physical reason §7b's writeup gives for its own night reading — `curSkyGlow` is nearly zero after
dark, so the term this feature grades has almost nothing left to redistribute regardless of BFS depth. The
`ambient_light_compat.json` regression pass (Ambient Light installed) read `ambient_sky_fraction`
0.458333343 at noon and 0.0183333326 at night — identical to the pre-`SkyFalloffSource` figures §7b's own
writeup quotes, and identical to this fix's own noon/night fractions above — confirming both that the
dispatcher's deferral doesn't perturb the compat path it wraps, and that the two independently-implemented
BFS paths now agree at the one cell directly comparable between them.

**Re-measured after the default moved to 25f** (see "Two sliders" above): same door-adjacent cell, same
scenario. Depth is unchanged at 2 (`MaxDepth` didn't move), giving fraction 0.2083 at noon against 0
off — exactly `1.0 * 0.25 * (1 - 2/12)`, and exactly proportional to the old 0.4583 by the 25/55 ratio,
confirming the slider path and the settings-default path compute identically. Cropped to the room
interior, whole-crop median CIELAB ΔE is **0.0** — most of the crop is unaffected pixel-for-pixel, since
25% only lifts the near-door cells into visibility. Splitting the crop the same way as above: a strip
right at the door reads median ΔE **3.64** against feature-off ("visible on close inspection", not "at a
glance"), a strip at the room's far wall reads **1.65** (barely visible) — a real ~2x gradient, but a
gentler one than 55f's, as intended.

**Case that surprised us:** the composite screenshot (`Tests/Screenshots/native_sky_falloff/`) still
reads as a broad, even lightening across the whole room rather than a glow concentrated tightly at the
door, even at 25%. This is not a bug — `MaxDepth` (12, unchanged) is close to an 11×11 room's own
diagonal, so nearly every interior cell sits within reach of the BFS regardless of how low
`PassThroughPercent` is set; lowering that knob scales the whole gradient down uniformly rather than
steepening it near the opening. A player who wants the effect visibly concentrated at doorways rather
than merely dimmer needs to lower `MaxDepth` too (the "How far the glow reaches" slider) — this is
exactly why that slider exists as a second, independent knob rather than folding "strength" and "reach"
into one.

**Moved into the preset bundle, per further live playtest feedback.** The 25f/12 default above was a
single global value shared by both presets; live play under each preset found that one number cannot
serve both. `PresetKnobs` gained `NativeSkyFalloffPassThroughPercent`/`NativeSkyFalloffMaxDepth`
alongside `MinIndoorBrightness`, for the same reason that floor is bundled rather than a standalone
slider: the two terms compound. Cinematic's `MinIndoorBrightness` sits at 0.50, so every roofed cell is
already lifted most of the way toward legible before this term runs at all — a 25%-strength opening glow
barely moves the needle against that floor, and the opening stops reading as distinct from the rest of
the room. Realistic's floor is 0, so the same 25% (or even less) is doing the floor's whole job by
itself and reads as intended. Landed on Realistic 35%/8 cells, Cinematic 80%/10 cells — both slightly
different from the single 25f/12 the section above measured, chosen by playing under each preset
directly rather than derived from the floor values. `NativeSkyFalloffMath.DefaultPassThroughPercent`
(25f) and `DefaultMaxDepth` (12) are unchanged and still exist — they are `NativeSkyFalloffSettings`'
own neutral fallback and the value `NativeSkyFalloffMathTests`' pinned test still checks — but neither
preset ships them anymore; `CelestialLightingSettings`' field initializers and `ExposeData` defaults now
read `Presets.Cinematic.NativeSkyFalloffPassThroughPercent`/`MaxDepth` instead, matching how
`minIndoorBrightness`'s own default already tracks the shipped preset rather than
`IndoorOcclusionMath.DefaultMinIndoorBrightness`. Both sliders switched from a plain `LabeledSlider`/
`LabeledIntSlider` to `AestheticSlider`/`AestheticIntSlider` (the latter newly added, mirroring the
former for the one `PresetKnobs` field that is an int rather than a float) so nudging either now flips
the preset radio to Custom, matching every other preset-bundle-backed slider.

**Superseded §7b's flat `DoorSkyLeak`, which was removed.** That older cap brightened a door-touching
corner by a fixed amount regardless of distance from the opening; this gradient does the same job
better — it scales with distance, so a threshold reads brighter than a cell three tiles into the room
the way `DoorSkyLeak` never could — and composes through the identical `CapOcclusion` term, so keeping
both would only ever double-count or fight, never improve on either alone. See §7b's "Doors are
boundary cells, not interior" bullet for the removal and its one accepted gap: a doorway with both this
feature and Ambient Light off gets no brightening at all, since nothing else fills that role anymore.

### 7d. Door strength dims the native flood (`DoorLeakMath` / `NativeSkyFalloffGrid`)

**Problem.** §7c's BFS treats every door as an equally-open threshold — a plain wood door and Anomaly's
Security Door hand the flood on with the same one-step cost, so a bunker behind a blast door reads
exactly as bright inside as a house behind a wood one. A sturdier door should leak less light, and
should do so independent of what it happens to be built from: a plasteel wood-frame Door and a wood
one are the same `ThingDef`, and a player who never builds anything but ordinary doors should see no
change at all.

**Approach.** `DoorLeakMath.CrossingMultiplier` turns "how much stronger than a wood door" into an
exponential attenuation on the flood's *strength*, not its *distance*:

```csharp
ratio = doorMaxHitPoints / referenceMaxHitPoints;
extraRatio = max(ratio - 1, 0);
multiplier = clamp01(exp(-sensitivity * extraRatio));
```

`NativeSkyFalloffGrid.Rebuild` carries a second `strengths` array alongside its existing `depths` one,
seeded at 1.0 for every unroofed cell and propagated forward as a running product — each cell's
strength is its nearest neighbour's strength times `CrossingMultiplier` if the neighbour being entered
is a door, or times 1 for ordinary floor. `FractionAt` multiplies `NativeSkyFalloffMath`'s existing
depth/maxDepth falloff by this strength as a final term.

- **Deliberately not extra BFS distance.** An earlier version of this feature had a stronger door cost
  extra BFS steps, which reads as pushing the room *behind* the door farther away — a security door
  made the room look deeper than its geometry, not just darker. What a stronger door should change is
  how much light survives the crossing, not how far it then has to travel: the room stays exactly as
  deep as its walls say, same as behind a plain wood door. Explicit design constraint from the feature
  request this exists for: the door must change the flood's *initial strength*, never the number of BFS
  steps. Depth and strength are therefore two separate arrays computed in the same pass, never folded
  into one.
- **`ThingDef.BaseMaxHitPoints`, not `Thing.MaxHitPoints`.** RimWorld has no stat that means "how much
  light a door should block" — max HP is the closest existing proxy for "how substantial is this door
  *type*", and reading it at the `ThingDef` level (`stuff: null`) means a plasteel Door and a wood Door
  read identically, since `StatWorker.GetValueUnfinished` gates its entire stuff-factor/offset block
  behind `req.StuffDef != null` (decompiled to confirm). Deliberate: a Security Door should leak less
  because it *is* a sturdier door type, not because a player happened to build an ordinary one from a
  pricier material — and pulling in stuff would need a matching table per stuff category with no way to
  stay exhaustive against modded stuff.
- **`ThingDef.blockLight` gates the whole formula, checked before the ratio math.** Strength was never
  the right question for a door that is not opaque: RimWorld's own glow grid already has a flag for
  exactly this (`Building.SpawnSetup`/`DeSpawn` call `map.glowGrid.LightBlockerAdded`/`Removed` gated on
  `if (def.blockLight)`, decompiled to confirm). `DoorBase` defaults it `true`, and no vanilla or DLC
  door currently overrides it — the gap only shows up with a modded see-through door (a glass door,
  motivating case), which sets it `false`. `CrossingMultiplier` returns `1` immediately when
  `blocksLight` is `false`, before touching `doorMaxHitPoints` at all, so a door that is both strong and
  see-through still passes fully rather than merely dimming less than an equally strong opaque one —
  light does not care how sturdy a pane of glass is. Deliberately not folded into the ratio (e.g. "treat
  a see-through door as if it had the reference's own HP") because that would still leave some dimming
  on a door whose actual `BaseMaxHitPoints` sits above the wood reference. No live scenario for this one
  — no glass-door mod is installed locally to spawn one against — the offline `DoorLeakMathTests` cases
  are the only proof (`CrossingMultiplier_BlocksLightFalse_PassesFullyRegardlessOfHitPoints` and
  `..._OverridesEvenAnExtremelyStrongDoor`, the latter pinned at `AncientBlastDoor`'s own hit points to
  prove the gate short-circuits ahead of the exponential, not blended into it).
- **A glass *wall* gets the same `blockLight` gate, but as a passthrough, not a multiplier — and this
  reopens a case §7's own header records as measured inert, in the one situation where it is not.**
  `IsWall` now also requires `blockLight`, so a see-through wall (`holdsRoof` true, `blockLight` false)
  no longer counts as a wall at all: the BFS crosses it exactly like open floor, one step of depth, no
  strength cost, since `CrossingMultiplier` only ever special-cases `AltitudeLayer.DoorMoveable` and
  leaves every other edifice at its default 1. This is deliberately a passthrough (empty space), not a
  door-style dimming crossing — a pane of glass is not a sturdier or flimsier obstacle the way a door is,
  it simply is not one. §7's "A bespoke transparent-wall leak was built for this and deleted" note
  (above) describes the *same* change, and it is not a contradiction: that measurement was taken with
  ReBuild loaded, and ReBuild's own `GroundGlowAt` patch makes it an `UnderRoofFalloffOwner`, which stands
  this entire BFS down map-wide before the branch could ever run. It could not have been anything but
  inert there. The live case this reopens is a glass wall from a mod that does NOT own the whole gradient
  — Vanilla Furniture Expanded - Architect's `VFEArch_CellWall` (`holdsRoof` true, `blockLight` false, no
  `GroundGlowAt` patch of its own) — where §7c's BFS is the only thing that ever runs, and previously
  read the wall as solid rock regardless of `blockLight`. `IndoorGlowPassthrough` still wins outright
  wherever another mod's gradient answers (`SkyFalloffArbitration`'s deferral is unchanged), so this only
  ever fires in the gap passthrough cannot reach — a genuinely new case, not a second implementation of
  the same fix. Live-verified in `Tests/Scenarios/glass_wall_leak2.json` (`VFEArch_CellWall` + its
  `VanillaExpanded.VFEArchitect`/`OskarPotocki.VanillaFactionsExpanded.Core` dependency, neither installed
  by any other scenario in this suite): a granite-walled control room reads `wall_control_depth` 0 (never
  reached) against a same-shaped room with one wall cell swapped for `VFEArch_CellWall`, which reads
  `glass_wall_depth` 2 and `glass_wall_fraction` exactly matching `door_strength_leak.json`'s own
  `wood_door_fraction` at the same depth (0.262499988 at noon, 0.0104999989 at 23:00) — confirming the
  crossing is genuinely strength-free, not merely dimmed less. Median CIELAB ΔE (CIE76) between the two
  rooms' interiors (clear of the wall sprite's own vertical bleed, which otherwise swamps the comparison
  with the two defs' unrelated base textures) is 1.10 at noon — visible on close inspection — and ~0 at
  23:00, where the fraction itself is too small to read on screen despite being real.
  **Kept out of `.glow`, on purpose.** `NativeSkyFalloffGrid.FractionAt` feeds only
  `SkyFalloffSource.FractionAt`, consumed solely by `Patch_IndoorSkyOcclusion`'s `CapOcclusion` argument
  — the cosmetic vertex-alpha term `SectionLayer_LightingOverlay` paints, never `map.glowGrid`. A glass
  wall reading as passable here changes how much of the *sky's colour* an indoor cell shows, not how
  bright the room actually is for gameplay purposes (pathing, plant growth, mood, `Room` mechanics all
  still see the wall as solid) — the same visual/atmospheric-only boundary this repo holds everywhere
  else it writes `.glow` at all (§7's own "few patches write `SkyTarget.glow`" scope note).
- **The reference is a wood door, and ratio 1 there is load-bearing.** `DoorStrengthReference` memoizes
  `ThingDefOf.Door.BaseMaxHitPoints` (160, `DoorBase`'s own `statBases` override, not the 100 `StatDef`
  default) once — defs never change at runtime, so re-deriving it on every `Rebuild` would pay a stat
  walk this value can never actually move on. A door *weaker* than the reference (Odyssey's
  `VacBarrier`, 100) simply keeps the flat 1.0 multiplier rather than brightening the flood — this knob
  only ever dims, matching "standard wooden doors should behave as they do currently" from the feature
  request that specified this. Worked examples from decompiled `BaseMaxHitPoints` values, sensitivity at
  its default 0.5 (`DoorLeakMathTests`): `OrnateDoor` (250, ratio 1.5625) keeps ≈0.75 — a mild dimming
  for a merely fancier door; Anomaly's `SecurityDoor` (800, ratio 5.0) keeps ≈0.135; Odyssey's
  `AncientBlastDoor` (6000, ratio 37.5) decays to effectively opaque. Two crossings in series (nested
  airlocks) compound multiplicatively, since `strengths` is a running product rather than a per-cell
  lookup.
- **`doorStrengthSensitivity` is a plain slider, not a preset-bundle knob.** An all-wood-door game must
  read identically at every preset, so moving this must never flip the preset radio to Custom — the same
  reasoning `polarNightBlueStrength`/`purpleLightStrength` already apply to their own per-effect
  intensities that sit outside the Cinematic/Realistic taste axis. `LabeledSlider`, range 0–2 (0 is the
  feature's own off-switch, reproducing the flat pre-§7d multiplier of 1 for every door regardless of
  strength). `IndoorOcclusionRedraw.SyncTo` already forces the whole-map `GroundGlow` rebuild §7b's own
  "Baked, not per-frame" bullet describes for `MaxDepth`/`PassThroughPercent`; it was extended to watch
  this field too, since it feeds the identical baked alpha through `SkyFalloffSource`.
- **Only weighs the native BFS, never the passthrough.** `SkyFalloffSource` defers to
  `IndoorGlowPassthrough` outright whenever another mod (Ambient Light) answers — that mod's own door
  handling is out of scope here, the same reason the two sources are never merged (§7c's "Deferral, not
  composition" bullet). A player running Ambient Light sees no door-strength effect from this mod; the
  settings slider's own label says so ("no Ambient Light").

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

- `ColorTemperatureKelvin(elevation, pressureFraction)` — a monotonic linear ramp from `HorizonKelvin`
  (2000 K, at/below the horizon) to `ZenithKelvin` (5772 K, at/above `DaylightAltitudeDegrees` = 60°).
  The warm endpoint is a *sea-level* endpoint: §20 slides it toward neutral by how much air the map's
  own tile actually has overhead. Since §20c this is honestly the **clean-air** curve: aerosol's
  colour is a spectral shape no colour temperature can carry, so it is applied per channel afterwards
  and the composed sky colour is deliberately not a blackbody at any temperature.
- `SkyColorForElevation(elevation, pressureFraction, aerosolFraction, angstromExponent, inVacuum)` —
  the composition the adapter and the probes both call: the clean-air blackbody above, multiplied by
  §20c's per-channel aerosol transmission. Anything that needs the sky's *colour* rather than its
  temperature goes through this rather than calling `BlackbodyToRgb` directly.
- `BlackbodyToRgb(kelvin)` — the widely published, public-domain Tanner Helland approximation of the
  Planckian locus (a standard tabulated/curve-fit conversion, textbook not mod-specific — see
  "Clean-room provenance"). Split into three small per-channel functions so the piecewise structure
  reads top-to-bottom.
- `TintStrength(elevation, pressureFraction)` — the geometric blend factor in `[0, 1]`: the product of a low-sun ramp
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
redden through. See §18 for why both halves are needed, and §20 for the continuous version of the
same argument: vacuum turns out to be the `pressureFraction → 0` endpoint of the site-altitude
curve, which is why the two are pinned against each other rather than merged.

This subsystem's `NightFadeFloorDegrees` (−6°) is also where **§19 takes over**: below the horizon
the warm tint hands off to ozone (Chappuis) twilight blue, which ramps in from −4°. The two overlap
deliberately across that 2° window, because real dusk has a warm band low in the west under an
already-blue vault. §19 is emphatically *not* an extension of this curve — it models an absorption
notch rather than Rayleigh reddening, and it inverts both of this file's tested invariants
(monotonicity, and R ≥ G ≥ B). See §19.

That 2° window is also **§19c's entire domain** — the twilight purple light. The handoff above turns
out to model the two sources as *substituting* for one another when in reality both are fully present
at once, which is why the window read as a muddy neutral rather than as the lavender a real dusk
shows. `NightFadeFloorDegrees` is `public` for exactly that reason: §19c reads the boundary from here
rather than writing −6 down a second time. Note also that §19c refutes, with arithmetic, the natural
idea that §8's blackbody and §19's notch should compose *multiplicatively* — §8's blackbody is a
source colour, and read as a transmission it eats blue about five times harder than green, which is
one of the two reasons that construction can never produce a green minimum. See §19c.

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

**§21 needs nothing from this subsystem, and that is the design.** The surface-cloud cavity raises the
night floor over a snowy map, and §9 keys its rod-vision ramp on brightness — so a snowed-in map
desaturates *less*, automatically, with no term of §21's own. That is also physically right: snow is
achromatic and shifts no hue while it desaturates less. §21 therefore writes **no saturation term**;
a second desaturation input would have been a second source of truth for a number this subsystem
already owns. See §21.

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

**The classifier has a second consumer now (§21).** `WeatherDimming.CloudOpacityFor` is the `a_cloud`
in §21's cavity — the term that decides whether the light snow throws upward comes back down. That
makes the opacity a *physical* quantity as well as a rendering one, and it means everything §13 built
to classify modded content reaches §21 for free: a mod author who attaches `WeatherCloudDeck` to say
"my heat shimmer is not a cloud deck" is telling both subsystems, from one place. It also raises the
cost of a misclassification slightly — a false overcast now brightens a snowy map's night floor as
well as darkening its sky — which is another reason `Tools/WeatherAudit` is the thing to re-run after
a mod-list change. See §21.

**One exception, added by issue #100 and widened by issue #134.** §21's cavity — the night floor
through `SurfaceBuildup.CloudOpacityOrClear`, and since #134 the daytime arm too through
`WeatherDimming.DeckOpacityFor` — substitutes §22's continuous cloud-cover fraction for this
classifier's reading **while the map's current weather is Clear**, because this classifier has no
opinion at all there: it scores Clear as 0 on both axes by construction, which §22 later made a stale
abstention rather than a true "no cloud". The substitution reaches the *cavity* only. **This section's
own dimming is untouched and still reads exactly 0 on Clear**, since a clear sky does not darken the
ground and §22 renders its own sky tint separately. Every other weather is unaffected either way. See
§21's and §24's own writeups.

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

**The third fill term, not yet built (§21).** Sea level has skylight, vacuum has the night budget —
and snow is the third, strongest fill of the three. The surface-cloud cavity makes illumination
near-isotropic, so shadows flatten and directional shading dies: the whiteout that makes terrain hard
to read in snow. That is a *contrast* effect and this file is where it belongs, but it is per-cell
(`SnowGrid.GetDepth`) and therefore sits on §16's ledger. §21 shipped the whole-map ambient half only
and records the deferral; nothing in this file changed for it.

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
| 15 (≈ −4°, onset) | 0.537, 0.639, 1.000 | 0.537 | 10.8% |
| 27 (≈ −7.2°, peak) | 0.327, 0.447, 1.000 | 0.327 | **24.2%** |
| 45 (≈ −12°, plateau) | 0.155, 0.261, 1.000 | 0.155 | 35.1% |

**How the last column is derived** (issue #89 — the onset row read 18.3% from §19's first commit
until it was recomputed; the other two rows were right all along, and nothing downstream ever quoted
the bad one). It is not `1 − blend·(1 − R/B)`; that naive form ignores the adapter's rescale and
gives 20.8% at airmass 15, which is why re-deriving it from the table alone fails. The actual chain
is four steps, exactly what `Patch_PolarNightBlue.BlendTowardHue` does to vanilla's Clear night sky
(0.482, 0.603, 0.682), read through the multiply overlay:

```
target  = (R/B) · vanillaB          rescale the normalised hue to the source's brightest channel
blended = vanillaR + (target − vanillaR)·0.45
ground  = 1 − blended / vanillaR    the overlay multiplies, so the player sees the RATIO
```

At airmass 15: `target = 0.5375·0.682 = 0.3666`, `blended = 0.482 − 0.1154·0.45 = 0.4301`,
`ground = 1 − 0.4301/0.482 = 10.8%`. The same three lines give 24.2% at airmass 27 and 35.1% at 45,
and 5.3% for the full lerp to the 20,000 K blackbody quoted above — one formula reproduces every
attenuation figure in this section. `TransmissionTable_ReproducesTheDocumentedFigures` pins all
three rows offline so the table cannot drift from the code again.

Two things the onset row is *not* saying. The 10.8% is the hue's effect **at full band strength**,
whereas `BandStrength(−4°)` is exactly 0 — the envelope opens at −4° and the transmission column
describes the hue that is waiting there, not what is on screen at that instant. And 18.3% was not
arbitrary: it is this same chain evaluated at airmass ≈21, i.e. −5.6°, the midpoint of the −4°→−7.2°
fade-in. A row computed one step further down the ramp than its own label, most likely.

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
- **vs §19c (purple).** That same 2° overlap is §19c's whole domain, and §19c's finding is that the
  cross-fade described above is the wrong *model* of it: the warm band and the blue vault coexist
  rather than substituting, so superposing them at full strength produces the green minimum neither
  subsystem can reach alone. §19c reads `BlueOnsetDegrees` from this file as its upper boundary and
  changes nothing here — below −6 and above −4 it is exactly zero. It also consumes
  `ChappuisTransmission`, `OzoneColumnForLatitude` and the cross sections unchanged; the standing
  ban on site-altitude and aerosol inputs to §19 is untouched, since §19c takes those on §8's side of
  the composition only. See §19c.
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

## 19c. Twilight purple light (`PurpleLightMath` / `Patch_PurpleLight`)

Fifteen to twenty-five minutes after sunset a clear sky can turn lavender. It is the most striking
thing an ordinary sky does, it is common enough to be familiar, and before this the mod could not
produce it under any conditions at any latitude.

**Purple is not a colour temperature.** No point on the Planckian locus is purple, which is why §8's
ramp can never reach it however it is tuned. In channel terms purple is a **green minimum**: red and
blue both high, green pushed below both. Everything below exists to produce that one ordering, and
the tests pin the ordering rather than any particular colour.

### Why the obvious construction cannot work, and why that is worth writing down

Issue #85 proposed composing §8's reddening with §19's ozone notch **multiplicatively**: sunlight
takes a long low path through the troposphere (reddened, §8), the already-reddened light crosses the
stratospheric ozone layer (green notched out, §19), and red-rich-minus-green is purple. It is a good
story. It is wrong twice over, and both refutations are kept executable (`SeriesGreenNotchIsReachable`,
`ShadowHeightKm`) rather than left as prose, precisely so nobody re-derives the idea in a year.

**First, the geometry — and this is the deeper one.** At solar depression `θ` the Earth's shadow
stands at `h = R⊕ · (sec θ − 1)`:

| solar elevation | shadow height |
|---|---|
| −4° | 15.6 km |
| −5° | 24.3 km |
| −6° | 35.1 km |

The troposphere tops out near 10–12 km. **Across the whole window it is already dark.** So light
that crosses the ozone layer does not afterwards cross a long tropospheric slant path — it drops
near-vertically through about one airmass — and the reddened light that makes the warm band low in
the west arrives along a completely different, near-horizontal sightline hundreds of km toward the
still-lit sunset point, which never enters the ozone slant path. **The two media are not in series
along one ray. They are two different rays into two different parts of the sky.** Sources in series
multiply; sources in parallel add. The correct composition is a sum, and the ticket's central
premise is a geometry error rather than a tuning one.

**Second, the colorimetry, which holds even if you grant the series reading.** Composing filters
adds optical depths, so the composed depth is `s · tropo + m · ozone` for some pair of weights. A
green minimum in transmission is a green *maximum* in depth, needing both `D_G > D_R` and
`D_G > D_B`. Ozone attenuates red harder than green — its cross section peaks at 603 nm, essentially
on the red channel centre — so it pushes the wrong way on the first. §8's blackbody attenuates blue
about five times harder than green in log terms, so it pushes the wrong way on the second. Each
needs the other to rescue it, and neither can:

| | R | G | B |
|---|---|---|---|
| ozone depth per airmass (45°) | 0.04221 | 0.03067 | 0.00081 |
| §8's 2000 K blackbody as a transmission | 1.000 | 0.565 | 0.060 |
| …as optical depth per unit §8 strength | 0 | 0.5711 | 2.8075 |

The pair of inequalities collapses to a band on the single ratio `s/m`, and at sea level that band is
**(0.0202, 0.0134)** — lower bound above upper bound, **empty**. Note what dropped out: the absolute
strengths. The condition is purely one of *shape*, so no amount of turning either subsystem up or
down can change the answer. This is the same family of result PR #98 established for a monotone
`λ^-α` filter — a monotone filter cannot cut a mid-band notch — extended to the composition of two
monotone filters of opposite slope, which can only do it when their slope ratios bracket. These miss
by a factor of about 1.5.

**The one hole, and why it closes.** The band *does* open above a horizon endpoint of ~2181 K, which
§20's site-altitude term reaches above roughly 1150 m. That is a real gap in the refutation and it is
kept under test rather than hidden. It closes on its own: the required `s` is then 0.35–0.71, against
a §8 `TintStrength` that never exceeds 0.32 anywhere in the window **and that §20 scales down by the
very `pressureFraction` that opened the band**. The two conditions pull in opposite directions, so
there is no map anywhere on which the series construction produces purple. §20b's pollution makes it
strictly worse, widening the gap rather than closing it — so the one case a reader might expect to
rescue it is where it fails hardest.

### A third diagnosis in the issue that also turned out to be false

Issue #85 described the current behaviour as "two independent additive tints toward the same sky"
that "blend the purple away into a muddy neutral", with a single composed lerp as the fix.
Successive `Color.Lerp`s toward **fixed** targets are algebraically **one** lerp toward the
weight-averaged target: with `W = 1 − (1 − t₈)(1 − t₁₉)` and

```
S = [ t₈(1 − t₁₉)·warm + t₁₉·blue ] / W
```

the two forms agree to float precision, which `SequentialLerps_AreOneCompositeLerp` pins. Nothing is
lost to sequencing that a composed lerp would have kept. The structure was never the problem. (The
one genuine non-commutativity, §19's rescale reading the intermediate colour, is a second-order
effect on the brightest channel, not on hue ordering.)

### What the problem actually is: amplitude, and a handoff that models the wrong thing

The window is precisely where **both** subsystems are weakest. §8 fades out by −6 while §19 fades in
from −4, so their strength product peaks at **0.048** at −5°. The cross-fade models the two sources
as *substituting* for one another — and physically they do not. At −5° the reddened horizon band and
the ozone-crossed vault are both fully present in the sky at once. **Cross-fading them is the error.**

Against vanilla's real palette the consequence is stark. Vanilla's Clear `skyColorsNightMid` and
`skyColorsNightEdge` carry the *same* triple `(0.482, 0.603, 0.682)`, and glow is
`clamp01(sin(elevation)/0.7)`, so below +4.01° vanilla's sky colour is that **constant** at every
elevation. Put through §8 and §19 alone, the whole window comes out green-above-red — plain blue,
with no green deficit anywhere:

| elevation | §8 + §19 only | ordering |
|---|---|---|
| −4.4° | (0.525, 0.583, 0.614) | B > G > R |
| −5.0° | (0.486, 0.566, 0.640) | B > G > R |
| −5.6° | (0.447, 0.546, 0.665) | B > G > R |

### The fix: a third superposition term

`PurpleLightMath` composes the two source spectra and the adapter applies the result as one further
luminance-neutral nudge. Three parts, none of them tuned:

**The window** is exactly `SkyColorTemperature.NightFadeFloorDegrees` to
`OzoneTwilightMath.BlueOnsetDegrees`, read from those two constants rather than written down again —
this subsystem is *defined* as their overlap, so if either boundary moves it must move too. Real
purple light peaks around −4 to −6, so the boundaries §8 and §19 already agreed on turn out to be the
physically right ones. Issue #85 explicitly allowed widening the window if a live A/B wanted it; it
did not.

**The envelope** is `4r(1−r)` — the simplest function that is zero at both ends, peaks at 1 in the
middle, and has no free parameter. Both zeros are load-bearing. Zero *outside* is what makes every
sunset the mod already shipped bit-identical. Zero *at the edges* removes the seam: §8 is still at
~0.39 at −4 and §19 at ~0.63 at −6, so an abrupt switch-on would put a visible step in the middle of
a live dusk. A trapezoid with a plateau was rejected — §19's plateau expresses dwell time at polar
latitudes and there is no equivalent argument here; the purple light is genuinely transient, and a
hump says so.

**The mix weight is solved, not chosen.** The warm source is red-dominant and the blue source is
blue-dominant, so mixing at weight `w` gives peaks `(1−w) + w·blue.R` and `(1−w)·warm.B + w`. Setting
those equal — *neither source dominates* — has one solution:

```
w = (1 − warm.B) / ((1 − warm.B) + (1 − blue.R))
```

and that solution is also, to a rounding, the mix that **maximises the green trough**. That is not
luck. Green is the one channel neither source carries — the warm source is green-poor because
reddening walked it down the blackbody curve, the blue source is green-poor because Chappuis eats
500–700 nm — so red and blue each arrive from one source only, and any mix favouring one dims the
other's peak toward green's level and fills the trough in. The physical reading is the honest one:
**the purple light is the moment neither the reddened band nor the blue vault dominates the western
sky**, which is exactly why it is a narrow window rather than a phase of dusk.

The composed target is normalised to a peak of 1, so it carries hue and nothing else, and the
adapter rescales it to the colour it is blending *from* — §19's convention, for §19's reason. The
sanity bound issue #85 asked for (never more selective than the more selective input) is structural:
a normalised convex combination cannot leave the hull of its endpoints. `w` runs 0.671 at −4 to 0.609
at −6, and the trough runs 31/255 to 44/255.

| elevation | composed target hue | green trough |
|---|---|---|
| −4.0° | (1.000, 0.878, 1.000) | 31/255 |
| −5.0° | (1.000, 0.851, 1.000) | 38/255 |
| −6.0° | (1.000, 0.826, 1.000) | 44/255 |

**The blend strengths are derived too.** If both sources are fully present, the composed
displacement from the vanilla palette is the union two full-strength sequential lerps would produce:

```
SkyBlend     = 1 − (1 − 0.35)(1 − 0.45) = 0.6425
OverlayBlend = 1 − (1 − 0.25)(1 − 0.30) = 0.4750
```

§8's and §19's constants became `internal` so this *reads* them rather than copying them, and a
retune of either carries here automatically.

### Lane, ordering and vacuum

Colour-only, never `.glow`, never `.saturation` — the same lane as §2, §8 and §19. Unlike §19 there
is **no brightness arm at all**: §19 needed an overlay floor because polar twilight is nearly black,
and civil twilight is not. Purple is a hue claim and nothing else; turning saturation up to sell it
would be a different subsystem making a different claim.

**No `HarmonyPriority`**, which cannot be expressed intra-assembly anyway (all our patches share one
owner ID). Ordering is secured *structurally* instead: the envelope is exactly zero at both
boundaries and the nudge targets a hue rescaled to whatever colour it finds, so running before §8/§19
would only let them blend it back down — weaker, never wrong or discontinuous. That is a far weaker
dependency than a priority attribute would be papering over.

**Vacuum**: `inVacuum` is threaded into the pure layer as a parameter per §18a, not early-returned in
the adapter. The purple light superposes two atmospheric scattering sources, neither of which exists
on an airless world, so like §19 — and unlike §8, which still pins an honest unreddened
`ZenithKelvin` — the whole effect is simply zero.

### Verification

Offline: `PurpleLightMathTests`, 70 cases. The envelope swept at 0.01° across the whole sky for
exact zero outside the window, a continuity sweep across both boundaries, symmetry about the
midpoint, the structural pin that the bounds *are* §8's and §19's, the green minimum and its depth,
the red/blue balance with a non-vacuity companion showing `w` really is a solve, the selectivity
bound, the latitude deepening at fixed elevation, the vacuum gate — and the refutation: the empty
band at sea level, pollution widening it, the ~2181 K opening kept honest, and the strength wall
that closes it swept across the whole window at three thin-air pressures.

**Live, and the timing is the trap.** At latitude 45 on day 11 the window is about **0.13 hours
wide** — hours 20.10 to 20.24, roughly eight game-minutes. §19's own post-mortem already warned that
an hourly grid straddles a 0.6–0.8 h band completely; this is five times narrower again, and an
hourly scenario would read "the effect is absent" everywhere. `Tests/Scenarios/purple_light.json`
was written only after a 31-sample survey at **0.05 h resolution**, and it pins `sun_elevation`
beside every effect probe so a future §14 clock change fails loudly rather than silently re-emptying
the capture.

| hour | elevation | window strength | composed hue green |
|---|---|---|---|
| 20.05 | −3.21° | 0 | 1.0 (outside) |
| 20.10 | −4.05° | 0.0998 | — |
| 20.15 | −4.89° | **0.988** | 0.8548 |
| 20.20 | −5.73° | 0.4625 | 0.8339 |
| 20.25 | −6.58° | 0 | 1.0 (outside) |

Two rules from §19's post-mortem are followed rather than restated: survey at ≤0.25 h before
choosing an hour, and pin `sun_elevation` beside every effect probe so a future §14 clock change
fails loudly instead of silently re-emptying the capture.

The A/B across the feature toggle at hour 20.15, on the shipped default preset — these are the
**measured** material readings, not predictions, and they read lower than `colors.sky` itself because
§7a darkens the composed material afterwards:

| probe | §19c off | §19c on | |
|---|---|---|---|
| `purple_sky_red` | 0.2662 | **0.3271** | red climbs |
| `purple_sky_green` | 0.2848 | 0.2884 | green essentially still |
| `purple_sky_blue` | 0.3130 | 0.3130 | **exactly unchanged** |
| ordering | B > G > R | **R > B > G** | green becomes the minimum |

**Blue does not move at all, and that falls out of the construction rather than being arranged.**
The composed hue's blue channel is pinned at 1 by the balance solve, and the adapter rescales the hue
to the source colour's brightest channel — which, on vanilla's night palette, *is* blue. So the
target's blue equals the source's blue and the lerp has nothing to do. §19c moves red and green only,
and the two feature-off/feature-on `purple_sky_blue` readings above are byte-identical for that
reason rather than by luck.

**Where the lavender ordering actually lives, and it is not where the arithmetic first suggested.**
Issue #85 asked specifically for `B > R > G`. At the window *peak* the effect is strong enough that
red overshoots blue and the ordering is `R > B > G` — magenta-leaning rather than lavender. A first
attempt to find `B > R > G` sampled the −5.73° shoulder on the reasoning that a weaker envelope would
leave red under blue, and that was **wrong in play**: the underlying sky is bluer down there too, so
red falls back under *green* as well and the ordering returns to `B > G > R` with no deficit at all.
The lavender band is in between, and it is narrow:

| hour | elevation | live `colors.sky` (R, G, B) | ordering | green minimum |
|---|---|---|---|---|
| 20.15 | −4.89° | 0.3271, 0.2884, 0.3130 | R > B > G | yes |
| 20.16 | −5.06° | 0.3235, 0.2872, 0.3177 | R > B > G | yes |
| **20.17** | **−5.23°** | **0.3161, 0.2856, 0.3225** | **B > R > G** | **yes** |
| 20.18 | −5.40° | 0.3042, 0.2834, 0.3273 | B > R > G | yes |
| 20.20 | −5.73° | 0.2650, 0.2761, 0.3370 | B > G > R | no |

So `B > R > G` occupies roughly −5.2° to −5.5°, about **one game-minute** at this latitude, and the
scenario pins hour 20.17 for it explicitly. The invariant that holds across the whole useful part of
the window is the weaker and more meaningful one — **green is the minimum channel** — which is what
purple means; whether red or blue is on top is the difference between magenta and lavender, and a
real dusk walks through both.

Rendered frames, measured with `Tools/FrameDelta/frame_delta.py` (new — the harness has no
comparison tier, `delta` asserts are still unimplemented, and every ΔE quoted elsewhere in this
document was computed by hand and thrown away):

| pair | median CIELAB ΔE | |
|---|---|---|
| `purple_off.png` vs `purple_on.png` | **4.14** | "visible at a glance"; mean 4.54, p90 5.54 |
| `purple_outside_off.png` vs `purple_outside_on.png` | **0.00** | **0 of 2,073,600 pixels differ** |

That second row is the invariant, not a formality: outside the window the two frames are
byte-identical at full resolution, so every already-measured scenario pin in this document is
untouched by construction rather than by tolerance.

On the reference scale this document already keeps — §20c 0.36, §19b 1.48, §20 1.88, §21 6.06,
§20b 6.79 — 4.14 sits between §20 and §21: comfortably past the ~2.0 "visible at a glance"
threshold, and deliberately short of the two biggest, since this fires for eight minutes of every
dusk rather than for a whole weather state.

**And the hue verdict, which for this subsystem matters more than the magnitude.** A large ΔE that
stayed on the Planckian locus would mean the sky merely got warmer — a failure wearing a good number,
and the one failure mode a colour-temperature subsystem is most likely to hide behind. Mean frame
chromaticity moved from **Duv +0.00168** (above the locus, green side) to **Duv −0.01226** (below it,
purple/magenta side): a **sign flip**, which is exactly the thing §8's blackbody ramp is incapable of
at any temperature, because every point it can reach is *on* the locus by construction. For scale,
neutral grey is +0.0032, vanilla's night sky +0.0125, and reference lavender −0.046.

`ApiCompatibilityTests` needs no new assertions — `WeatherWorker.CurSkyTarget`, `SkyTarget.colors`,
`SkyColorSet.sky`/`.overlay` and `MatBases.LightOverlay` are all already pinned by §2, §8, §7a and
§19.

#### Filming the sweep, because a still cannot show a transient

Every number above is a point measurement, and a point measurement is exactly what a reader should
distrust for a claim of this shape. A still at 20.17 asserts a one-game-minute transient; it cannot
show one, and it is indistinguishable from the best frame of a sweep somebody kept quiet about. So
the sweep itself is filmed: `Tests/Scenarios/purple_light_timelapse.json` runs the harness
`Timelapse` step twice over the same third of an hour, once with `purple_light` off and once on,
and `Tools/Timelapse/compose_ab.py` pairs the two sequences frame-for-frame into
`Tests/Screenshots/purple_timelapse_ab.mp4` (and a downscaled `.gif`, the only thing GitHub will
animate inline from a raw URL).

**`stepHours` is `0.004`, and the specific value is load-bearing.** The earlier 0.05 h survey grid
steps about three game-minutes per frame, which renders the whole phenomenon as a single flicker —
the same mistake §19's post-mortem warned about, one order finer. 0.004 h is 10 ticks *exactly* at
`GenDate.TicksPerHour` = 2500, which matters because `AdvanceTime` rounds hours to whole ticks: a
step that lands on a half-tick sheds it every frame, and over an 80-frame sweep that drift is a
respectable fraction of a 0.13 h window. The sweep runs 20.00 → 20.32, so roughly 0.10 h of ordinary
sky precedes the window and 0.08 h follows it — arrival and departure are both in shot rather than
implied.

What the film shows, measured from the rendered PNGs rather than asserted:

| frames | hours | off vs on |
|---|---|---|
| 1–24 | 20.004–20.096 | byte-identical |
| **25–53** | **20.100–20.212** | **differ — 29 frames, peaking at 20.172 (−5.26°)** |
| 54–79 | 20.216–20.316 | byte-identical |

So 50 of the 79 published frames are byte-identical and 29 are not, and the 29 are contiguous. That
is the outside-the-window invariant restated as a *film* rather than as a pair of stills, which is
the form in which cherry-picking is not available.

One honest wrinkle, recorded because it looks like a counter-example and is not: frame 0 of the two
sweeps also differs, by 1.3 % of pixels confined to the top 115 rows, with channel deltas up to 248.
That is the run's first screenshot still carrying UI chrome — alert text, the top bar, the learning
helper — that hidden-UI mode had not finished tearing down; terrain pixels across the frame are
byte-identical there. `compose_ab.py --skip-first` drops it. Left in, it would paint a false
"frames differ" mark on a frame well outside the window, which is the opposite of the point.

### What is deliberately not done

- **No stratospheric aerosol term.** Real purple light is strongly aerosol-dependent and spectacular
  after a major eruption (post-Pinatubo 1991 is the canonical case). §20b's aerosol is a
  *boundary-layer* species and correctly does nothing here — haze at 1500 m scale height mutes the
  purple rather than driving it. The volcanic case needs a *stratospheric* species with a low
  Ångström exponent, which is #83's territory and a separate ticket.
- **No widening of the window**, per the paragraph above: the live A/B did not ask for it, and issue
  #85 was explicit that the boundaries are physically chosen rather than arbitrary.
- **No change to §19's cross sections.** Sampling red at 600 nm, on the Chappuis peak, is what makes
  ozone attenuate red hardest, and a properly band-averaged red cross section — the sRGB red primary
  integrates out to 700 nm, where Chappuis is transparent again — would be materially lower and would
  change the shape arithmetic above. It would also change §19 everywhere, breaking the bit-identical
  invariant this ticket was pinned on. Filed as **#112**, which is also the ticket that would
  reopen the shape half of the refutation above: lowering the red cross section is precisely the
  move that pushes on the `D_G > D_R` condition, so if the band-averaged value moves red far, the
  empty `(0.0202, 0.0134)` band is worth recomputing before it is cited again.


## 20. Site altitude — scaling §8's reddening by the observer's air column (`AtmosphericColumn` / `SiteAltitude`)

§8 keys the sky's colour temperature on **sun altitude** and nothing else: a linear ramp from
`HorizonKelvin` (2000 K) at/below the horizon to `ZenithKelvin` (5772 K) at 60°. Latitude and season
already enter that curve correctly and implicitly, because they change where the sun goes. What the
curve did not model is where the *observer* is. Every map got the same 2000 K sunset whether it sat
on a sea-level swamp or on a 4000 m plateau.

**The physics.** Rayleigh optical depth is proportional to the number of air molecules in the light
path, and for the path that matters here that reduces to the surface pressure **at the observer**.
The slant path from a low sun descends out of space and *terminates* at the observer's altitude — it
never re-enters the denser air below. So the entire dense column beneath a mountain base is skipped
rather than merely traversed at a shallower angle, and the mountain genuinely sees:

- a whiter sun disk that stays white further down toward the horizon,
- a **less** saturated sunset — high-altitude sunsets are famously subdued, not more vivid, which is
  the one part of this that surprises people,
- a deeper blue vault at midday (not modelled here; §8 is a warm-end curve).

**The model** is the barometric/exponential atmosphere, one constant:

```
pressureFraction = exp(-siteAltitudeMetres / 8500)
```

| tile elevation | `pressureFraction` | effective horizon Kelvin |
|---|---|---|
| 100 m (vanilla `Tile.elevation` default) | 0.988 | 2016 K — imperceptible |
| 1500 m | 0.838 | 2237 K |
| 4000 m | 0.625 | 2649 K |
| ∞ | 0 | 5772 K — the vacuum endpoint |

Most maps are therefore unchanged, and the effect appears only where the terrain justifies it — the
same shape as §19's "polar night emerges with no polar special case": nothing is switched on by a
threshold, the curve simply reaches somewhere different when the tile does.

### Where it enters the curve, and why both halves again

`pressureFraction` enters `SkyColorTemperature` twice, and the reason is the same one §18a spells
out for vacuum.

1. **`HorizonKelvinForColumns`** (named `HorizonKelvinForPressure` until §20b gave it a second
   species to compose) slides the *warm* endpoint from `HorizonKelvin` toward
   `ZenithKelvin`. Only the warm endpoint moves: 2000 K is what a full sea-level column *does to*
   sunlight, while 5772 K is the unreddened photospheric anchor — sunlight before the atmosphere
   touched it — so there is nothing for thinner air to move the neutral end toward. Thinning the air
   can only ever walk the reddened end back up to the anchor, which is why this interpolates between
   exactly those two existing constants and introduces no third one.
2. **`TintStrength`** is multiplied by the same fraction. Pinning the Kelvin alone would not weaken
   the effect, for precisely the reason §18a records: the Helland fit puts `ZenithKelvin` at roughly
   `(1.00, 0.95, 0.90)` rather than at white, so a thin-air sky would still be blended toward a
   faintly amber target at full strength. Scaling the strength is what produces the *subdued*
   mountain sunset. Physically the two factors are the same optical depth seen twice: less air to
   redden the beam, and less air to carry the reddened colour into the sky.

**Linear in mireds, not in Kelvin.** A mired is 10⁶/K. The usual reason to work there is
perceptual — equal mired steps read as equal shifts, which is why photographic filters are graded in
them — but that is not the reason here. The reason is that **a mired shift is approximately linear in
optical depth**, and optical depth is exactly what `pressureFraction` scales, since Rayleigh optical
depth is proportional to the air column overhead.

So the whole endpoint model falls out of the single statement *reddening is proportional to column*:

| | |
|---|---|
| mired shift at sea level | `10⁶/2000 − 10⁶/5772` = 500.0 − 173.2 = **326.8** |
| mired shift at 4000 m | `0.6247 × 326.8` = **204.2** |
| horizon endpoint at 4000 m | `10⁶ / (173.2 + 204.2)` = **2650 K** |

Linear-in-Kelvin, which this subsystem originally shipped, has no comparable derivation — it was a
first-order artistic choice, and it put 4000 m at ~3416 K, walking the warm end back nearly twice as
far for no stated physical reason.

Both spaces agree **exactly** at both endpoints (`p = 1 → 2000 K`, `p = 0 → 5772 K`), so every
invariant that lives at an endpoint — including §18's requirement that `p → 0` reproduce the vacuum
value — is untouched by the choice. Only the interior moves.

The ramp this feeds is still a linear Kelvin lerp **on elevation**, so the two spaces do coexist in
one file. That is deliberate rather than sloppy: elevation moves the sun *through* the column, a
geometric path-length effect with its own airmass curve, while `pressureFraction` moves the column's
*density*. They are different physical quantities, and there is no reason to expect one interpolation
space to serve both.

### Why the vacuum gate stays separate

At `pressureFraction = 0` the ramp pins flat to `ZenithKelvin` and the tint goes to zero — *exactly*
the pair of values §18's discrete gate returns. Vacuum is the `h → ∞` limit of this same curve, so
§18a's special case stops being unexplained and becomes an endpoint a continuous model independently
agrees with.

It does **not** follow that the gate should be replaced by `pressureFraction = 0`. Two reasons:

- They read **different data for different questions**. `Vacuum` reads `BiomeDef.inVacuum` — "does
  this map have an atmosphere at all" — while `SiteAltitude` reads `Tile.elevation` — "how much of
  one is above this particular spot". An orbital platform's tile still carries an `elevation` field,
  and deriving airlessness from it would mean trusting a number that means nothing up there.
- §18's convention exists precisely so the gate **cannot be forgotten**: `inVacuum` stays the last
  parameter, required and never defaulted, early-returning at the top before any atmospheric math
  runs. Folding it into a float that has a perfectly ordinary default would give every future call
  site a silent way to opt out of it. `pressureFraction` is therefore threaded in *ahead* of
  `inVacuum`, leaving the vacuum parameter exactly where every other subsystem's is.

The agreement is instead cashed in as a free offline test
(`ZeroPressure_ReproducesTheVacuumValuesExactly`, asserted as exact equality rather than within a
tolerance — both paths land on the same constants by construction, so if they ever only *nearly*
agree, one side has grown arithmetic the other has not).

### §19 is deliberately altitude-invariant

§8 and §19 must diverge here, and the asymmetry is pinned in one test
(`OzoneTwilightBlue_IsAltitudeInvariant_WhileTheWarmTintIsNot`) so they cannot drift into each other.
§8's Rayleigh reddening happens in the air the observer is standing in, so a mountain skips most of
it. §19's Chappuis absorption happens in the **ozone layer at 20–30 km**, entirely above any
mountain: the absorbing column over a 4000 m plateau is the same column as over the beach next to it,
so polar night blue must not scale with site altitude. Since `OzoneTwilightMath` expresses that
invariance structurally — by having nowhere to put such a term — the test asserts it structurally,
which is the form that fails when someone adds one.

### The shared column model (`AtmosphericColumn.cs`)

The file is deliberately *not* a Rayleigh-only helper. It exposes `ColumnFraction(altitude,
scaleHeight)` with `RayleighScaleHeightMetres = 8500` as one named caller, because different
scatterers have very different scale heights and the next one is already specified: aerosols (dust,
smoke, haze) are injected near the ground and settle out, so they hug the surface with H ≈ 1500 m and
fall away nearly six times faster with altitude — which is exactly why mountain air looks *clean*
rather than merely thin. §20b below is that species, added exactly as predicted — a second constant
plus two accessors in this same file, with no new exponential and no new `<Compile Include>` entry.

8500 m is Earth's textbook value (kT/mg for dry air near 250 K) and is used without apology: RimWorld
planets are Earth-analogues down to the biome list, and there is nothing in worldgen a per-world
constant could be sourced from. The unit tests pin it against real measured anchors — Denver 1600 m →
0.83 atm, Lhasa 3650 m → 0.65, Everest 8850 m → 0.35 — which is the point of preferring one physical
constant to a hand-tuned ramp.

Sub-sea-level sites clamp to 1 rather than exceeding it. The physics does keep going (the Dead Sea
shore genuinely sits under ~1.05 atm) but every consumer treats 1 as "the full, unmodified sea-level
effect" and is tuned against that ceiling — §8 multiplies the fraction straight into its 0.35/0.25
per-channel blend maxima — so a hard `[0, 1]` contract is worth more than a 5% over-pressure that no
RimWorld worldgen produces.

### The naming hazard

**`elevation` already means SUN elevation in this subsystem.** `Patch_SkyColorTemperature` opens with
`float elevation = SolarPosition.ElevationForMap(map)` and every function in `SkyColorTemperature.cs`
is keyed on `elevationDegrees`. RimWorld's field for terrain height is, unhelpfully, also called
`elevation`. So the name is dropped at the boundary: the value is `siteAltitudeMetres` from the
moment it is read, and what reaches the curve is `pressureFraction`. This is not fussiness — two
different quantities called `elevation` three lines apart would make every subsequent line of both
files ambiguous to read, and the mistake would be invisible in a diff.

### The impure boundary (`SiteAltitude.cs`)

Shaped exactly like `LatitudeEffect.cs`: one live read off the world grid, converted to a primitive,
handed to a pure model that owns the math. `Tile.elevation` is a plain public float on **base**
`RimWorld.Planet.Tile` (not the Odyssey-era `SurfaceTile` subclass — verified by decompiling 1.6
`Assembly-CSharp`), already in metres, so no DLC gate and no cast. Two new
`ApiCompatibilityTests` assertions pin it: `Tile_HasElevation` (exists, public, on base `Tile`,
`System.Single`) and `PlanetLayer_HasIsRootSurface` (the guard that goes with it).

It is its own file rather than a private method on the patch because the live probe has to read the
*same* value the patch does — §18's rule that a probe reads the same gate as its patch, for the same
reason: a probe reporting a colour temperature the sky is not being tinted toward is worse than no
probe. `sky_color_temperature` therefore now reports the tile's own horizon endpoint (~2649 K on a
4000 m tile), so a mountain scenario can pin the whole chain end-to-end.

Two guards, both returning 0 m — sea level, `pressureFraction` 1, bit-identical to the pre-§20 curve,
because the right default for "cannot honestly answer" is the behaviour the mod already shipped:

- **Non-surface `PlanetLayer`.** Orbital-ring tiles carry `elevation` because it is on base `Tile`,
  but it is not a terrain height there. §18's vacuum gate short-circuits space maps before the value
  is ever used; this guard is what keeps that belt-and-braces rather than an unstated dependency on
  two independently-motivated gates always agreeing.
- **No world tile at all.** Pocket maps (vanilla's undercave, a Biomes! Caverns cavern) index out of
  the layer's tile list. §8 never reaches this on those maps (`MapSky.IsEnclosed` returns first), but
  `SiteAltitude` is shared and must not throw for a caller that has not made that check. Note the
  read goes through the `PlanetLayer` indexer rather than `Find.WorldGrid[tile]`: the two reach the
  same `Tile`, but the layer's bounds-checks and returns null while `WorldGrid`'s subscripts the
  backing `List<Tile>` unchecked and would throw on `PlanetTile.Invalid`.

### Compat

Realistic Planets 2 and Planetsmith both overhaul worldgen and may shift the elevation distribution
(see `PlanetsmithCompat.cs`). The model degrades gracefully — an unexpected range just moves along
the same curve, and the curve is monotone and bounded at both ends — so no compat branch is
warranted; a sanity check on a generated world is worth doing when one of them is next loaded.

**They shift `Tile.rainfall` too, which matters more than elevation does here.** RP2 replaces
vanilla's noise-based rainfall with a moisture pipeline — `MoistureLayer`,
`MoistureAdvectionLayer`, `OrographyLayer`, `RainfallLayer`, `AridityLayer` — every stage of which is
player-tunable through its `ClimateConfiguration` (orographic windward/leeward strength, rain-shadow
decay, ocean e-folding distance, and so on). Rainfall is the keying axis for §20d's Ångström exponent
(`AerosolSpectrum.AngstromExponentForRainfall`) and §20e's background aerosol
(`AtmosphericColumn.BackgroundAerosolFraction`), so a player who turns their humidity knobs is
moving our sunset colour. The same graceful-degradation argument covers it: both readers clamp
between vanilla's own ExtremeDesert (340 mm) and rainforest (2000 mm) breakpoints and are monotone
inside them, so an unfamiliar distribution slides along the curve rather than off it.

**Cloud cover is the same story, and more directly than rainfall's other two consumers.** §22's
partial cloud cover is not a reading of the current `WeatherDef` — it computes a fraction, and
`SeasonalWetFraction` computes it from exactly the two quantities RP2 rewrites. `CloudCoverClock`
reads `tileInfo.rainfall` into each `WeatherDef.commonalityRainfallFactor`, and gates each entry on
`GenTemperature.GetTemperatureFromSeasonAtTile`, which reaches `SeasonalShiftAmplitudeAt` — a method
RP2 patches outright. So an RP2 world's humidity and its seasonal temperature curve both land on our
cloud fraction, and from there on §21's cavity and §23's underlighting.

Nothing needs doing about that, and the reason is worth stating rather than assumed: both inputs are
read through vanilla's own API surface at the moment they are used, not cached from worldgen, so
RP2's rainfall writes and its `GenTemperature` prefixes are picked up automatically. The pattern
holds for every one of §20d/§20e/§22's rainfall consumers — we key on vanilla quantities, and a mod
that changes those quantities changes what we render without either side knowing about the other.

(An earlier draft of this block asserted that nothing here computes cloud cover at all. That was
written against a working copy that predated §22 by a fortnight, and it is exactly the drift this
file's own preamble warns about — the code wins.)

A `SiteAltitudeStrength` slider (0–2, default 1.0 = physical) is deliberately **not** shipped up
front. Whether the honest range reads too subtle in play is a question for a live A/B, not for
design, and adding a knob before that is answered would bake in a guess.

### Out of scope, filed separately

- **Biotech `Tile.pollution` as aerosol loading.** Opposite sign to altitude (more reddening), but
  Mie scattering greys rather than reddens, so it collides with §9's desaturation and needs its own
  design rather than a second multiplier here. **Landed as §20b** — and the collision was real: the
  reddening half shipped, the greying half did not.
- **Latitude-keyed ozone column for §19.** ~260 DU in the tropics vs ~400 DU at high latitudes in
  spring, so Chappuis blue should strengthen toward the poles. A genuine colour-by-location effect,
  but it belongs to §19's absorption-notch model, not §8's Rayleigh one — and note it is a *latitude*
  term, not a site-altitude one, so it does not contradict the invariance pinned above.
- **§7 starlight extinction.** `k = 0.28` mag/airmass is a documented **sea-level** constant
  (§18b) with exactly the same site-altitude problem — nearer 0.12–0.15 at 4000 m — and it would use
  this same `AtmosphericColumn` model.

## 20b. Pollution aerosol loading — a boundary-layer species with its own scale height (`AtmosphericColumn` / `SiteAltitude`)

> **Superseded in part by §20c.** Everything below about the aerosol *column* — the 1500 m scale
> height, the 5.7× decay ratio, `Tile.pollution` as loading, the mountain sitting above the smog —
> is current and unchanged. What §20c replaced is the last step, where that column became a
> **colour**: the `AerosolHorizonKelvin = 1500 K` endpoint and the second Kelvin lerp described under
> "Why a third Kelvin constant here" no longer exist. Aerosol's colour is now per-channel
> transmission in `AerosolSpectrum`, because a point on the Planckian locus cannot carry a spectral
> shape. The section is kept as written because the reasoning that produced 1500 K is what calibrated
> the optical depth that replaced it. Read it, then read §20c.

§20 gave §8's sunset a second input: how much *air* the observer has overhead. It left the obvious
companion question unasked — what is *in* that air. Biotech writes a per-tile `pollution` value in
0–1 that, until now, had no effect on this mod's sky at all. Physically it is aerosol loading, and
aerosol is the third absorbing species over a map, after Rayleigh (§8/§20) and stratospheric ozone
(§19).

**The physics, and why this is not simply "turn §8's warm knob up on polluted tiles".** Aerosol
differs from Rayleigh in two ways, and both of them matter.

1. **Its scale height is ~1.5 km, not 8.5 km.** Aerosol is not a component of the air the way
   nitrogen is. It is *injected* at the surface — fires, dust, sea spray, industry — and continuously
   removed by settling and rain-out, so its vertical profile is set by that source/sink balance in
   the boundary layer rather than by the hydrostatic balance that gives bulk air its 8500 m. Measured
   continental values cluster around 1–2 km; 1500 m is the middle of that band.
2. **Mie scattering is far less wavelength-selective than Rayleigh.** Rayleigh goes as λ⁻⁴; aerosol
   is closer to λ⁻¹ or λ⁰ depending on particle size. Heavy aerosol therefore does **not** simply
   push the sky further down the Planckian locus — it *greys and mutes* it. A polluted sunset is a
   dimmer, browner, lower-contrast orange, not a more vivid one.

**Point 1 is the whole reason this is worth building.** 8500 / 1500 = 5.67, so the aerosol column
decays with altitude nearly six times faster than the air column does. The two terms then compose
into something neither could produce alone:

| tile | `pressureFraction` | `aerosolFraction` | effective horizon Kelvin |
|---|---|---|---|
| sea level, pollution 0 | 1.000 | 0.000 | 2000 K — the original curve, untouched |
| sea level, pollution 0.5 | 1.000 | 0.500 | 1333 K |
| sea level, pollution 1.0 | 1.000 | 1.000 | 1000 K — the new warm endpoint |
| 100 m (vanilla default), pollution 1.0 | 0.988 | 0.936 | 1033 K |
| 1500 m, pollution 1.0 | 0.838 | 0.368 | 1537 K |
| 4000 m, pollution 1.0 | 0.625 | 0.069 | 2379 K |
| 4000 m, pollution 0 | 0.625 | 0.000 | 2649 K |

Read the last two rows together: full pollution on a 4000 m tile moves the horizon endpoint by 133 K,
against 500 K at sea level. **A mountain base is above the smog.** And because pollution warms while
altitude cools, a polluted lowland and a clean plateau sit at opposite ends of one continuous curve
with no threshold and no special case anywhere — the same shape §19's polar night and §20's mountain
sunset already have.

### Where it enters the curve — one place, not two

§18a and §20 both needed *both* halves of §8 (the Kelvin endpoint and the tint strength), and both
recorded why at length. §20b needs only one, and the asymmetry is the design rather than an
oversight.

`HorizonKelvinForColumns(pressureFraction, aerosolFraction)` replaces §20's
`HorizonKelvinForPressure` and stacks two lerps rather than widening one. The clean-air endpoint is
settled first — "how reddened is an unpolluted column at this altitude" — and the haze the observer
is standing in is then laid on top of whatever that came out as. The order is the physical claim, and
it has a useful structural consequence: the aerosol term vanishes on a mountain **without any
altitude logic living in the colour curve**, because `aerosolFraction` is itself an `exp(-h/1500)`
column that has already collapsed to 0.069 by 4000 m. The pure layer never learns what an altitude
is; it just receives a fraction that has already gone away.

`TintStrength` is deliberately **not** given the term, and the absence is asserted structurally
(`TintStrength_HasNoAerosolTerm_BecauseMieMutesRatherThanIntensifies`) so no future call site can
quietly start feeding one. Two independent reasons:

- **Where aerosol exists, the strength factor is already saturated.** The aerosol column is above
  half only below ~1040 m (1500 · ln 2), where `pressureFraction` is ≥ 0.88 and the horizon-time tint
  is already at or near its 1.0 ceiling. An additive strength term would be clamped away across most
  of the band it was added for — a knob that mostly does nothing.
- **Where it would *not* be clamped, it would be backwards.** Point 2 of the physics above: Mie
  scattering is nearly wavelength-flat, so heavy aerosol mutes a sunset rather than intensifying it.
  Pushing the tint stronger would model the effect with the wrong sign.

### Why a third Kelvin constant here, when §20 introduced none

§20 made a point of adding no new constant, and the argument was specific: thinning the air can only
ever walk the reddened end back *up* toward the unreddened photospheric anchor, so `HorizonKelvin`
and `ZenithKelvin` already bracketed everything the altitude term could reach. Aerosol moves the
other way — it adds optical depth, pushing the endpoint *past* the sea-level 2000 K into territory
neither existing constant describes. `AerosolHorizonKelvin = 1500` is that argument's mirror image
rather than a violation of it.

**Why it saturates instead of extrapolating.** The tempting move is to treat pollution as extra
optical depth and keep extending §20's linear Kelvin ramp. But that ramp is *calibrated* on [0, 1] —
one full sea-level air column is worth exactly 3772 K of reddening — and extrapolating a calibration
is not the same as using it. Taken literally it runs off the end of the world: a heavy urban aerosol
optical depth is several times the sea-level Rayleigh depth (τ_R ≈ 0.098 at 550 nm against an AOD
that can exceed 0.3), and even after discounting it for Mie's much weaker selectivity — Ångström
exponent ~1.3 against Rayleigh's 4, so roughly a third of the reddening per unit depth — the linear
form lands below absolute zero. Real extinction does not behave that way either: past a point, more
haze mostly dims and greys rather than reddening further.

**Why 1000 K specifically**, anchored on the Helland fit this subsystem already uses rather than on
taste: the fit's blue channel is pinned at 0 below 1900 K, so *all* of the travel from 2000 K down to
here happens in green — 0.537 at 2000 K, 0.425 at 1500 K, 0.266 at 1000 K — with red already
saturated. Losing half the green against a saturated red is precisely *browner*, the word the physics
reaches for when describing a polluted sunset. 1000 K is the fit's stated validity floor, so this
rides the edge rather than sitting comfortably inside it; lower would be extrapolating a curve fit
outside its published range, which is where this stops.

**It moved from 1500 K because 1500 K was invisible.** Measured on rendered frames, pollution 1.0
against clean air came out at a median CIELAB ΔE of **1.31** — below the ~2.0 threshold for "visible
at a glance". A maximally poisoned sky looked almost exactly like a clean one, which is not a
defensible place for a subsystem's *maximum* to sit. Deepening the endpoint took that to 2.60, and
the blend boost below took it to **6.79**.

### Why the suppression is 8×, not the 14× the fractions alone predict

Both stages interpolate in **mireds** (see §20 for why: a mired shift is approximately linear in
optical depth, and both fractions *are* optical depths). Stacking them has a consequence worth
stating outright, because deriving it from the aerosol fractions alone gives the wrong answer.

The aerosol fraction itself collapses **14.4×** between sea level and 4000 m (1.000 → 0.069). But the
shift is that fraction times the distance from the clean-air endpoint down to `AerosolHorizonKelvin`,
and altitude has already moved that endpoint *up* — so there is more room to redden into at altitude
than at sea level:

| | headroom to 1000 K |
|---|---|
| sea level | `10⁶/1000 − 10⁶/2000` = 1000.0 − 500.0 = **500.0 mired** |
| 4000 m | `10⁶/1000 − 10⁶/2649` = 1000.0 − 377.3 = **622.7 mired** |

so the net suppression is `14.388 × (500.0 / 622.7)` = **11.55×**. Thinner air gives back a little of
what the missing haze took away.

That is the claim holding rather than failing. 11.55× still sits far above the **5.67×** ratio of the
two scale heights, and lands close to the **8.99×** ratio of the two columns — so *a mountain base is
above the smog* survives the endpoint geometry; it is simply worth 8× rather than 14×. Pinned by
`PollutionsWarmingCollapsesWithAltitude_ButLessThanTheColumnAlone`, so a future retune of either
endpoint cannot move it silently, and so the next reader who derives 14× from the fractions finds out
here rather than from a screenshot.

### The greying half, deliberately not built

Point 2 of the physics says the dominant real effect of heavy aerosol is muting, not reddening. We
shipped the reddening and left the muting alone, on purpose.

Desaturation is §9's lane (`Patch_LowLightDesaturation` / `PurkinjeMath`), and §9 is already being
restacked under #78. Two subsystems independently pulling saturation down is *precisely* the failure
#78 exists to fix, so adding a second one while the first is mid-repair would be building the bug on
purpose. §8's lane stays colour-only for the same reason it always has: it writes `.colors.sky` and
`.colors.overlay` and touches neither `.saturation` (§2's) nor `.glow` (off-limits to the whole
lane).

So the honest statement of what shipped is: **§20b models the wavelength-selective part of aerosol
extinction and skips the wavelength-flat part.** If the mute reads as missing in a live A/B, that is
a §9 ticket keyed on this same `aerosolFraction` — the fraction is already computed and already
available at the boundary — to be filed once #78 settles. It is not a change to this section.

### Vacuum, and the consistency of the fraction pair

§18's convention is untouched: `inVacuum` is still the last parameter, still required and never
defaulted, still early-returning before any atmospheric math runs. `aerosolFraction` is threaded in
*ahead* of it, exactly where §20 put `pressureFraction`, so the vacuum parameter stays where every
other subsystem's is.

`HorizonKelvinForColumns` cannot enforce that its two fractions are a consistent pair, and there is
exactly one place that would matter: the `h → ∞` limit that §20 cashes in as the vacuum agreement. An
aerosol column that outlived the air column would drag the airless endpoint away from `ZenithKelvin`
and break `ZeroPressure_ReproducesTheVacuumValuesExactly`. It cannot, because aerosol is the
faster-decaying of the two — but "obviously" is not a test, and the guarantee lives in
`AtmosphericColumn` rather than in the curve, so it is asserted where the pair is actually produced
(`BothColumnsReachZeroTogether_SoTheVacuumAgreementSurvivesTheSecondSpecies`).

### Compat: a Biotech mechanic with no Biotech gate

`Tile.pollution` is a plain public `float` on **base** `RimWorld.Planet.Tile`, not on anything
Biotech adds — verified by decompiling 1.6 `Assembly-CSharp`. This is the identical situation to
§18's `BiomeDef.inVacuum`: all DLC code ships in the base assembly, so the field compiles and
evaluates with Biotech uninstalled and simply reads 0 on every tile. §18's rule therefore applies for
§18's reason — **no `ModsConfig.BiotechActive` plumbing**, because a DLC branch could only ever agree
with the field and would be a second thing to keep in sync.
`ApiCompatibilityTests.Tile_HasPollution` pins existence, base-type location and `System.Single`, the
same three things `Tile_HasElevation` pins for §20.

`SiteAltitude` grows a second accessor (`AerosolFractionForMap`) alongside `PressureFractionForMap`,
sharing one guarded `TileForMap` helper so the non-root-surface and null-tile guards exist once. Both
guards now mean "sea level, unpolluted" — `pressureFraction` 1 and `aerosolFraction` 0, still
bit-identical to the pre-§20 curve. The two accessors read the tile independently rather than
returning a struct: the read is a bounds-checked list index, so the duplication costs nothing
measurable, and one primitive out per question is the shape §20 deliberately gave the file.

No settings slider ships, for the reason §20 declined one: whether the honest range reads right in
play is a question for a live A/B, and adding a knob before that is answered bakes in a guess.

### What is pinned offline

- **`pollution = 0` is bit-identical to §20's behaviour**, asserted as *exact* equality over a
  sweep of altitude × sun elevation, with §20's one-line formula restated in the test rather than
  called — which makes it a pin on the behaviour rather than a tautology about the current code.
  Every tile in a game without Biotech takes this path, so "almost unchanged" would mean the mod's
  default behaviour had silently moved for every existing colony.
- **Monotonicity both ways**: warmth non-decreasing in pollution at fixed altitude, and — separately
  re-asserted with the new term switched on — non-increasing in altitude at fixed pollution. The
  second is not free: climbing raises the clean-air endpoint *and* thins the aerosol column, and the
  test is what says the two effects agree rather than a comment claiming they do.
- **The 5.7× ratio itself**, as `ln(aerosol) / ln(rayleigh) = 8500 / 1500` swept over altitude.
  Pinned as a sweep precisely because it is altitude-independent: a retune of either constant, or a
  replacement of either accessor with something that is not a pure exponential of the same height,
  diverges rather than moving one number.
- **§19 is pollution-invariant at every elevation**, asserted structurally the same way its
  altitude-invariance is. The ozone layer sits at 20–30 km — roughly fifteen aerosol scale heights up
  — so no boundary-layer haze is between the observer and the Chappuis absorption at all. This is the
  *stronger* of the two §19 invariants and deliberately does not catch a latitude term, which is a
  different axis (see below).

### Out of scope, filed separately

- **The §9 muting ticket**, described above: keyed on the same `aerosolFraction`, to be filed once
  #78 settles.
- **Pollution's effect on §7's night sky.** Urban haze is what actually kills starlight, and §18b's
  `k = 0.28` mag/airmass sea-level extinction constant has the same shape of problem §20 already
  flagged for altitude. Same `AtmosphericColumn` model, different subsystem.
- **Latitude-keyed ozone column for §19** remains §20's open item and is unaffected by this section —
  it is a latitude term in the stratosphere, where §20b is a pollution term in the lowest 1.5 km.
- **The column being a *fixed* function of the tile.** Everything above describes one number per
  tile, so two consecutive evenings on one map are identical. **Landed as §20c**, which drives this
  same `aerosolFraction` with a slow noise instead of replacing any of the model above.
- **Aerosol particle size.** This section models the aerosol's *amount* and gives it one fixed colour.
  Real aerosol's wavelength selectivity varies by an order of magnitude with particle size, which is
  what actually produces sunset variety. **Landed as §20d** — and it turned out not to be a term
  alongside `AerosolHorizonKelvin` but a replacement for it, for the reason recorded there.

## 20c. Day-to-day aerosol drift — why two evenings differ (`AerosolDrift` / `AerosolDriftClock`)

§20b left the aerosol column a **fixed function of the tile**: `pollution × exp(-h/1500)`, evaluated
once and identical forever. Two consecutive evenings on one map therefore produce a pixel-identical
sunset. That is exactly the monotony players notice even when any single sunset looks good in a
screenshot — and it is a different complaint from "the palette is too narrow". This section addresses
**repetition**; widening the range is a separate axis.

**Why real sunsets differ night to night.** It is not geometry: the sun's path is nearly identical
two evenings running, and §20/§20b's other inputs (site altitude, tile pollution) do not move at all.
What changes is the **air mass overhead**. Maritime air arrives clean, continental air arrives loaded,
a front brings a different particle size distribution, smoke drifts in from somewhere else entirely.
So the physical quantity that varies between evenings is the aerosol **loading**, and that is what
§20c varies.

### The model: one multiplier, mean 1

```
aerosolFraction = AtmosphericColumn.AerosolLoadFraction(h, pollution, background)  // §20b + §20e
                × (1 + amplitude · (2·fbm(t) − 1))                                  // §20c
```

`background` is §20e's addition, landed after this section — see below and §20e for why the third
argument exists and what it changed about the paragraph that follows.

`fbm` is `AuroraNoise`'s existing layered value noise, sampled on a time axis. Three properties fall
out of that shape rather than being tuned in:

- **Mean exactly 1.** `AuroraNoise.Fbm` is a convex combination of `Hash01` values and `Hash01` is
  uniform on [0, 1), so the field's mean is 0.5 and `2u − 1` has mean 0. Retuning the amplitude, the
  cell width or the octave count cannot move the baseline, because none of them touch that argument.
  This matters more than it looks: a drift that averaged 1.05 would mean every polluted map in every
  save was permanently hazier than §20b's physics says, with nothing to notice it by.
- **Strictly positive by construction.** `1 + a·u` with `u ∈ [−1, 1]` is positive for any `a < 1`,
  and `MaxDriftAmplitude = 0.9` is a hard ceiling on whatever a caller passes. Positivity is
  therefore a consequence of the arithmetic, not of a clamp somebody could remove.
- **Inert where the load is inert.** Multiplying is what makes a zero-load tile stay exactly zero, so
  §20c needs no gate of its own — whatever `AerosolLoadFraction` hands it, multiplying by 1 ± amplitude
  cannot manufacture a nonzero drift out of nothing. At the time this bullet was written that load was
  zero on every tile in a game without Biotech, which made "no Biotech" and "no drift" the same
  statement; §20e (below) gave every tile a small nonzero load instead, so the *gate-free* claim still
  holds exactly but the *no-Biotech-tiles-see-nothing* claim it used to imply no longer does — see the
  flip side immediately below, now updated for that.

That last property has a flip side worth stating plainly, because it bounds what this section can
achieve: **§20c is proportional to the tile's aerosol load, so at the time this section shipped it did
nothing at all without Biotech and very little on a lightly polluted tile.** On a `pollution = 0.5`
sea-level tile the ±35% moves §20b's horizon endpoint across roughly 1837–1662 K, which is a clear
difference between two evenings; on a `pollution = 0.05` tile it is about ±9 K, which is nothing. That
is the correct *physics* — an air mass carries more or less of what the local sources emit, it does
not manufacture haze over a pristine tile — but it meant "two evenings differ" was delivered only
where there was something to vary.

**Landed as §20e**, which gives every tile a rainfall-keyed background load independent of
`pollution`, summed into the same `AerosolLoadFraction` this section's multiplier already applies to —
so a non-Biotech tile now has *something* for the ±35% to act on, rather than 0 × anything. It is a
smaller something than the sea-level pollution example above, though: §20e's background sits well
under a full pollution column (see that section's own numbers), so `amplitude = 0.35`'s ±35% is ±35%
of a small number on a non-Biotech tile, not the wide swing shown above. Whether that reads as "a
little weather" or "nothing" in play is unmeasured — §20e's own live verification could not run in this
environment — and is recorded as that section's open follow-up (retune `DriftAmplitude` once it can be
measured) rather than answered by guessing at a bigger amplitude here. What did **not** happen is
§20b's clean-air baseline moving: §20e's background is additive on top of pollution inside
`AerosolLoadFraction`, not a replacement of the zero-pollution case, so this section's own "inert where
§20b was inert" property above is now stated too strongly by one word — multiplying by zero *pollution*
no longer means multiplying by zero *aerosol* — but the drift itself still needs no gate, since it
multiplies whatever load `AerosolLoadFraction` hands it regardless of source.

`amplitude = 0.35`. Sized against what it does to the thing a player sees rather than against
anything measured: larger values start producing evenings that look like a wildfire moved in, on a
tile with no wildfire.

### Correlation time is the whole design, and flicker is the failure mode

A column that wobbles inside a single evening does not read as weather. It reads as a bug, and it
would fight §8's elevation ramp — the ramp would be smoothly warming while its own endpoint slid
underneath it. Air masses persist for days, so the noise has to as well. Three constants carry that,
and all three are pinned:

| constant | value | why |
|---|---|---|
| `LatticeCellDays` | 3 | base correlation time; with the second octave the effective figure is ~2 days |
| `Octaves` | 2 | a third would put a component on an **18-hour** cell — inside one evening |
| `TicksPerSample` | 2500 (1 in-game hour) | sampling cadence, see the performance note below |

The measured behaviour at those constants, over the full 294912-sample period across six seeds:

| lag | mean \|Δ multiplier\| |
|---|---|
| 1 hour | 0.0033 |
| 4 hours (an evening) | 0.0130 |
| 1 day | 0.0724 |
| 2 days | 0.1177 |

A day moves the column **22×** further than an hour does, and the single **worst** hourly step
anywhere in the sequence (0.018) is still smaller than an *average* day's change. That last one is
the deterministic form of "weather, not flicker" and is the assertion that fails first if an octave
is ever added.

### Seeded off the tile, clocked off the absolute tick

The sequence is a pure function of `(PlanetTile.tileId, TickManager.TicksAbs)`. Both are values
RimWorld already persists, which is what makes save/load reproducibility structural rather than
something we maintain: there is no drift state in the save because there is nothing to save. A colony
reloaded gets the identical evening it had before the reload; two colonies on one planet get
independent weather histories because they sit on different tiles. `AuroraNoise` avalanches its seed,
so adjacent tile ids — which is what worldgen actually produces for neighbouring settlements — do not
produce visibly related fields.

The noise wraps at 4096 base cells (12288 days, ~205 in-game years). A period is not optional —
`AuroraNoise` is a tiling generator built for a scrolling texture — so it is a number we choose, and
choosing it also keeps the arithmetic exact: the sample index is wrapped in **integer** arithmetic
before it is divided into a lattice coordinate, so a colony in year 5600 gets the same lattice
resolution as one in year 5500 instead of slowly losing mantissa bits.

### Where it is applied, and the clamp at 1

At the **adapter boundary** (`SiteAltitude.AerosolFractionForMap`), not inside `AtmosphericColumn`
and not inside `SkyColorTemperature`. Two reasons:

- `AtmosphericColumn` answers "how much of species X is overhead given an altitude and a loading",
  which is a **timeless** question. Threading a clock into it would make every one of its callers
  pass a tick they do not have, for a term only one of them wants.
- `SiteAltitude` already reads live state, and applying it there means the live probe
  (`Probes/SkyColorTemperatureProbe.cs`) reports the **driven** fraction the sky is actually being
  tinted toward. That is §18's rule that a probe reads the same value as its patch, for §18's reason.

The driven fraction is clamped back into [0, 1], and that clamp is a real asymmetry rather than a
formality. On a maximally polluted sea-level tile the baseline is already 1 — the most aerosol the
model knows how to mean — so the *upward* half of the excursion has nowhere to go and is clipped,
while the downward half is not. That is the correct trade: `HorizonKelvinForColumns` lerps toward
`AerosolHorizonKelvin` on this fraction, and a fraction above 1 would **extrapolate** past 1500 K,
which is precisely the "runs off the end of the world" failure §20b's own argument for saturating
rather than extending is about. The baseline-preservation invariant is therefore pinned on the
**multiplier**, where it is a property of the model, and not on the post-clamp fraction, where it is a
property of how close one particular tile sits to the ceiling.

### Performance: what this costs per frame

`Patch_SkyColorTemperature` hangs off `WeatherWorker.CurSkyTarget`, which
`SkyManager.CurrentSkyTarget` evaluates **twice** per `SkyManagerUpdate` per map, and this mod hangs
several postfixes off it (§16 on what per-frame work costs in this codebase; issues #11, #12, #20,
#23, #60). An unmemoised two-octave fbm on that path would recompute, several times a frame, a value
that by construction has not changed since the last time it was computed.

So `AerosolDriftClock` memoises on the model's own hourly cadence:

- **Steady-state per-frame cost: one `Dictionary<int, …>` lookup on an int key plus one int compare,
  per call.** No allocation, no trigonometry, no world-grid access.
- **The noise itself runs once per tile per in-game hour** — at normal speed, once every ~42 real
  seconds — and is eight integer hashes plus a handful of lerps.

The hourly quantisation is the one visible cost, and it is not visible. The fastest hourly step is
0.018 of the multiplier, which on the worst possible tile is ~9 K of horizon endpoint; over that same
hour the sun moves ~15° and drags the endpoint by several *hundred* K continuously. The staircase is
around one percent of a motion already happening.

### Why the state is a static memo and not a `MapComponent`

The obvious shape for "a smoothly varying per-map value" is a `MapComponent`, and this mod's original
plan sketched exactly that for a cloud-cover subsystem that was never built. Two specific things argue
against it here:

1. **There is no state to persist.** The drift is a pure function of two values RimWorld already
   saves, so a `MapComponent` would add a scribe node carrying nothing the save does not contain.
2. **That node is permanent.** `Verse.Map.ExposeComponents` writes one `<li Class="…"/>` per component
   into every save, and deleting the type later logs two red errors per map on the next load — the
   exact trap `MapComponent_SunShadowAxis.cs` survives as a tombstone for. Taking on an unremovable
   save-format entry to hold zero state is paying that price for nothing.

The memo instead follows `SunClock.cs`, this mod's established pattern for "recompute on a slow
game-time cadence rather than per frame". It is keyed on the **tile id**, and that choice is what makes
it incapable of serving a wrong answer: the cached value is a pure function of exactly
`(tileId, sampleIndex)`, so a hit requires both parts of the key to match, which means the inputs
match, which means the value is right. Nothing a new game, a reload or a second colony can do changes
that — which is why, unlike `SunClock`, it ships no `Clear()`. (`SunClock`'s own `Clear()` is in fact
dead code: its header says it is called on load and nothing calls it. It is harmless for this same
reason, and noting it here is cheaper than leaving the next reader to wonder why one cache has a
teardown hook and the other does not.)

### The pure/impure split

`AerosolDrift.cs` is Verse-free and linked into the test project via `<Compile Include>`, so the
shipped code is the tested code. `AerosolDriftClock.cs` reads `Find.TickManager.TicksAbs` and
`map.Tile.tileId`, holds the memo, and contains no arithmetic. This is the same split as
`FrameStamp.cs`/`GeometryMemo.cs` and for the same reason: from inside a running game a memo bug and a
formula bug look identical, so the half that *can* be tested offline is kept where it can be.

Two `ApiCompatibilityTests` assertions go with the live half: `PlanetTile_HasTileId` (public `int`
field — also what `SunClock` keys on) and `TickManager_HasTicksAbs` (public `int` property; it must be
the **absolute** tick, since a counter that reset on load would give a reloaded colony a different
evening from the one it just had).

### What is pinned offline

- **Amplitude 0 is *exactly* 1**, asserted as exact equality over a sweep, plus `ApplyMultiplier(x, 1)
  == x` — so §20b's shipped behaviour is reproducible bit for bit with the drift off. Negative and
  NaN amplitudes collapse to the same disabled case; the NaN one matters most, since a NaN would
  propagate into the sky colour and Unity renders that as black.
- **Mean 1 over the full period**, pooled *and* per seed. Per seed as well as pooled because a single
  map is what a player experiences, and pooling could mask one permanently-hazy tile against one
  permanently-clean one.
- **Strictly positive and bounded for every amplitude**, swept over values a slider or a dev override
  could plausibly pass and values nothing should ever pass (5, 1e9, +∞). Plus the *structural* form:
  `MaxDriftAmplitude < 1`, which is what makes positivity a consequence rather than a coincidence of
  the samples the sweep happened to visit.
- **Evening ≪ day**, in three forms — the mean-change ratio (>10×, measured 22×), the deterministic
  worst-hour-vs-mean-day bound, and the *structural* cause: `LatticeCellDays / 2^(Octaves−1) ≥ 1`, so
  no octave's cell can fall inside a single night.
- **Determinism as order-independence.** "Same seed, same sequence across save/load" reduces offline
  to "no hidden state", since both live inputs are persisted by the game; the test walks the sequence
  backwards while interleaving a second seed and demands the identical values.
- **The wrap**, in both directions, so the integer reduction in `Field` and the periods handed to the
  octaves cannot drift apart — a mismatch would show as one discontinuity every 12288 days and never
  in testing.
- **The cadence**: one sample is exactly one in-game hour, a 60000-tick day is exactly
  `SamplesPerDay` buckets, and the index *floors* rather than truncating toward zero (C#'s `/` would
  otherwise fold the two buckets either side of tick 0 into one double-width bucket).

### Out of scope, filed separately

- **Widening the palette.** §20c makes a map walk around inside its range; it does not make the range
  bigger. That is a separate axis and a separate ticket.
- **A background aerosol term independent of Biotech pollution.** **Landed as §20e**, additively rather
  than by moving §20b's clean-air baseline (the risk this bullet originally flagged never
  materialised — see the flip-side paragraph above), and confirmed live (median ΔE 0.66–5.50 across four
  sampled tiles, with and without Biotech — see §20e's "Live verification" for the full table). What
  §20e did *not* do, and what remains open: a `DriftAmplitude` retune to size §20c against the smaller
  background load rather than only against a full pollution column. Filed as
  `Jeffrharr/CelestialLighting#108` rather than done inside §20e's own PR, since it needs its own live A/B
  measurement against the drift swing specifically, not the baseline load §20e already measured.
- **Driving anything else with the same clock.** Cloud cover, §7's starlight extinction and §19's
  ozone column all have day-to-day variability with the same shape. `AerosolDrift` is deliberately
  named for its one consumer rather than generalised up front; the second consumer is what should
  decide whether the seed/lag parameters become arguments.
- **A settings slider**, declined for the reason §20 and §20b both declined one: whether the honest
  range reads right in play is a question for a live A/B, and adding a knob before that is answered
  bakes in a guess.

## 20d. Aerosol particle size — taking §8 off the Planckian locus (`AerosolSpectrum`)

Everything §8 had before this section was a **one-parameter family**. The curve ramps along the
Planckian locus from ~5772 K to a warm endpoint, and §20 (site altitude) and §20b (pollution) each
added an input — but both of them only change **how far along that single curve** the sky travels.
The hue *path* never changes. Every sunset on every map was the same march toward the same orange,
with more or less of it. Real sunsets are yellow, ochre, brick, brown and crimson, and reaching any
of those means leaving the locus, which cannot be said in Kelvin at all.

**The mechanism.** Rayleigh scattering has one fixed spectral shape, λ⁻⁴, which is precisely why a
single colour temperature can summarise it. Aerosol has no fixed shape. Its wavelength dependence is
the **Ångström exponent** α in `τ(λ) ∝ λ^-α`, and α varies enormously with particle size, because the
size parameter `2πr/λ` is what decides whether a particle scatters selectively at all:

| α | particle regime | what the sun does |
|---|---|---|
| ~0 | fog droplets, sea spray, thick blowing dust | **grey** extinction — a white sun that merely dims |
| 0.5–1.0 | coarse mineral dust | brown/ochre, the Saharan-dust sunset |
| 1.2–1.7 | urban/industrial haze | the classic deep orange-red |
| 2.0+ | fine smoke, secondary sulfate | a deep, saturated red |
| 4 | (Rayleigh, the clean-air limit) | the reference case, not an aerosol |

The α ≈ 0 row is the one worth building for: on the locus it is **unsayable**, because there aerosol
*amount* and aerosol *colour* are the same knob and a full column always means a fixed amount of
reddening. Decoupling them is the new capability.

**The file precedent is `OzoneTwilightMath`**, this codebase's existing case of modelling a species by
per-channel transmission rather than by a colour temperature, reached for there because ozone cuts a
notch and here because aerosol's slope varies. The two files deliberately sample the **same three
wavelengths** (600 / 550 / 450 nm), so the two species are described in one basis rather than each
being right in its own private units.

### The decision this section turns on: subsumption, not composition

§20b already expressed the aerosol's colour, as a second lerp along the locus down to
`AerosolHorizonKelvin = 1000 K`. `AerosolSpectrum` expresses the **same physical effect** as
per-channel transmission. They are two representations of one thing, so applying both would
double-count the reddening and leave two uncoordinated aerosol colour paths in the subsystem. Exactly
one of them can be live.

**The direction is forced rather than chosen.** The locus representation is lossy in precisely the
direction this section needs. The Helland fit pins its blue channel at 0 below 1900 K, so §20b's
1000 K endpoint has blue **exactly zero** — and any α-dependent correction applied downstream of it is
multiplying zero. Composed in that order, the pale blue-retaining sun that a large-particle aerosol
actually gives would be structurally unreachable, i.e. the headline case would be unreachable.
Composed the other way, on the *clean-air* colour, the spectral model expresses every case including
§20b's own. So the per-channel model takes the aerosol's colour outright:

```
cleanAir = BlackbodyToRgb(ColorTemperatureKelvin(elevation, pressureFraction, inVacuum))
sky      = cleanAir * normalise(exp(-τ(λ) · aerosolFraction · lowSunFraction))
```

`AerosolHorizonKelvin` is retired to `AerosolSpectrum.CalibrationAnchorKelvin`, where it is no longer
a live endpoint but is still the number `HorizonOpticalDepth` was fitted to — a calibration whose
anchor has been deleted is a magic number one commit later.

Two stages, and **the order is a physical claim**: sunlight really does cross the bulk atmosphere
before it reaches the boundary layer the observer is standing in. The aerosol load is scaled by
`LowSunFraction` (this subsystem's existing ramp, not a new one) because optical depth is a path
length — which also makes the aerosol fade with sun altitude at *exactly* the rate §20b's Kelvin
endpoint faded at, since that endpoint was consumed by the same lerp.

### Calibration: this is a generalisation of §20b, not a retune

`HorizonOpticalDepth = 6.5514` is **fitted, not measured**. It is the τ for which the model at
α = 1.3 reproduces the green channel of the colour §20b shipped:

```
exp(-τ · [(550/550)^-1.3 - (600/550)^-1.3]) = G(1000 K) / G(2000 K)   →   τ = 6.55138
```

α = 1.3 is the right reference because **§20b was already implicitly calibrated there** — its own text
names the value when it justifies its endpoint ("Ångström exponent ~1.3 against Rayleigh's 4, so
roughly a third of the reddening per unit depth"). §20b had already chosen an α; it just baked it into
a constant instead of exposing it.

**The fit moves with the anchor.** This constant was originally 2.1931, fitted against §20b's first
endpoint of 1500 K. §20b then moved that endpoint to 1000 K, because at 1500 K a maximally polluted
sky measured a median CIELAB ΔE of only 1.31 against clean air on rendered frames — below the ~2.0
"visible at a glance" threshold. The green ratio the fit has to reproduce fell from 0.7909 to 0.4962,
which triples the required depth. Carrying 2.1931 across that move would have silently reverted two
thirds of §20b's deepening while still claiming to reproduce it, so the constant is **refitted**
rather than merged.

At that exponent the reproduction is: red exact (the fit saturates it), green exact by construction,
blue 0.0038 against §20b's hard 0. The residual is the fit's blue cliff, not a disagreement — and it
is six times *smaller* than the 0.0224 the 1500 K fit left, because a deeper column attenuates the
short wavelength harder. Behind a ≤0.60 blend it is under 0.003 of final sky colour.

A **literal** slant-path optical depth would be ~11 (a heavy urban vertical AOD near 0.3 across a
horizon airmass near 38), still not quite twice this. That is the right number for a
radiative-transfer renderer and the wrong one here, because the thing being reproduced is §8's 2000 K
sea-level endpoint, which is itself a first-order artistic anchor rather than a computed radiance.
Calibrating against the shipped look keeps the two consistent; calibrating against the literal physics
would silently move every existing sunset. Worth noting the refit moved this number *toward* the
literal figure rather than away from it: the deeper §20b endpoint is the more physically plausible of
the two, not merely the more visible one.

### What composes around this, and what would double-count

Two neighbours touch the same aerosol column and neither is folded into `HorizonOpticalDepth`.

**§20c's day-to-day drift** multiplies `aerosolFraction` before it ever reaches this file. It changes
the column's *amount*, hour to hour; §20d changes its *shape*, which is a fixed property of the tile.
They compose without interacting: a drifting load walks along one hue path, and which path that is
stays keyed on rainfall. The drift is already inside the fraction by the time `AerosolSpectrum` sees
it, so this file needs no knowledge of it at all.

**`Patch_SkyColorTemperature.AerosolBlendBoost`** (§20b) opens the adapter's per-channel blend from
0.35/0.25 up to 0.60/0.43 under a full column. That is a different quantity from optical depth, and
the distinction is what keeps them from double-counting: `HorizonOpticalDepth` decides **what colour**
the haze layer is, the boost decides **how completely** the sky takes that colour. §20d changed only
the first, and the refit above is precisely what keeps the reference-exponent target identical to the
colour the boost was measured against. Folding the boost into the depth instead would redden twice.

**The boost also has to be keyed on particle size**, and it was not. As §20b shipped it the boost was
keyed on aerosol *amount* alone, which is right for the half of the question §20b could see and wrong
for the half §20d introduced. The boost's argument has an unstated premise — that the layer *has* a
colour of its own to be approached. At α ≈ 0 it does not: grey extinction's transmission is exactly
`(1, 1, 1)`, so "the layer's own colour" is the clean-air colour, and opening the blend toward it only
amplifies §8's Rayleigh tint. Measured on rendered frames, a full grey-dust column came out with mean
red **rising** 103.6 → 109.1 against the clean control: visibly *warmer*, when the whole claim about
large particles is that they dim without colouring. That is the same "haze makes sunsets more vivid"
error `TintStrength` refuses to commit, arriving through the blend instead.

So the amount term is multiplied by `AerosolSpectrum.ChromaticFraction(α)`:

```
aerosolBlend = 1 + AerosolBlendBoost · aerosolFraction · ChromaticFraction(α)

ChromaticFraction(α) = clamp01( (τ_B(α) − τ_R(α)) / (τ_B(1.3) − τ_R(1.3)) )
```

The chromatic part of a Beer–Lambert column is the **spread** between its per-channel optical depths,
which is what a filter contributes to hue as opposed to brightness. It is exactly 0 at α = 0 by
construction rather than by tuning, and clamps to exactly 1 at and above the reference exponent.

Two properties make it safe to multiply into a constant someone else already measured:

- **The load cancels.** Both depths scale linearly with `aerosolFraction`, so the ratio does not depend
  on it. The boost is *already* keyed on load; a second load term would square the amount and quietly
  retune §20b. `ChromaticFraction` takes no load argument at all, and a test pins that signature.
- **It is exactly 1 from α = 1.3 up.** Every tile at temperate rainfall or wetter gets the boost §20b
  measured, bit-for-bit — verified live, not argued: the temperate and rainforest frames measure ΔE
  11.51 and 14.23 both before and after the taper. Only the dry half tapers, and the dry half is a
  range §20b had no way to express in the first place.

### Normalisation, and the muting half that is still §9's

The transmission is normalised to its largest channel before use, so it carries hue and not
brightness. This is `OzoneTwilightMath`'s argument repeated for its reason: §8 is a **colour-only**
lane, a sky colour carries brightness of its own, and an un-normalised transmission would darken it —
smuggling a brightness change into a patch that promises not to make one.

It also lands exactly on the split §20b already recorded. §20b shipped the wavelength-*selective* half
of aerosol extinction and skipped the wavelength-*flat* half (the muting and greying), because
desaturation is §9's lane and §9 is mid-repair under #78. §20d changes the **shape** of the selective
half and leaves that split precisely where it found it. So at α = 0 the aerosol correctly does nothing
to the colour; the dimming it would really cause is still #78's to deliver.

### Keying α to the map, and why on rainfall rather than on biome

The ticket recommended a biome-keyed lookup. `Tile.rainfall` is a better version of the same idea
rather than a departure from it: **vanilla assigns biomes by scoring rainfall and temperature.**
Decompiling 1.6 shows `BiomeWorker_Desert` gating on `tile.rainfall >= 600f`,
`BiomeWorker_ExtremeDesert` on `>= 340f`, and `BiomeWorker_TropicalRainforest` on `< 2000f`. Rainfall
*is* the axis the biome label is derived from, so keying on it keys on the same thing continuously,
with no `defName` table for a Biomes! or Alpha Biomes tile to fall off the end of. The ramp's two
breakpoints are vanilla's own.

The direction is the physically defensible part — arid ground lofts coarse mineral dust, which is
near-grey, while wet ground supplies the fine secondary and biogenic particles that strip blue hard:

| tile rainfall | α | the sunset |
|---|---|---|
| ≤ 340 mm (vanilla `ExtremeDesert` cutoff) | 0.20 | pale, dimmed, nearly colourless |
| 600 mm (vanilla `Desert`/`AridShrubland` cutoff) | 0.48 | ochre |
| 1000 mm | 0.92 | brown-orange |
| ~1354 mm | 1.30 | **exactly what §20b shipped** |
| ≥ 2000 mm (vanilla `TropicalRainforest` cutoff) | 2.00 | deep saturated red |

Note where 1.3 lands: inside the temperate/boreal band most colonies are founded in, so the sunset
most players already have does not move, and the new colours appear at the extremes. That is the
correct place for a variety feature to spend its budget.

Two robustness properties fall out. **α only matters where there is aerosol** — on a pollution-0 tile
the §20b column is zero and every α produces an identical (empty) transmission — so a snow-covered
cold desert being scored as "dust" costs nothing. And the `SiteAltitude` guard falls back to the
*reference* exponent rather than to 0, because unlike the two fractions beside it there is no identity
value here: α = 0 is not "no effect", it is a specific physical claim.

`Tile.rainfall` is a plain public float on **base** `RimWorld.Planet.Tile` in mm/year, exactly like
`elevation` and `pollution`, so it needs no DLC gate. `ApiCompatibilityTests.Tile_HasRainfall` pins
existence, base-type location and `System.Single`, the same three things its two siblings pin.

### What this honestly buys, and what it provably cannot

Stated carefully, because the ticket's own framing overreaches in one place and it is worth writing
down rather than rediscovering.

**It leaves the one-parameter family — unambiguously.** At α = 0 a *full* aerosol column produces no
hue shift whatsoever, which the locus model was structurally incapable of. That is the headline, and
it is pinned as exact equality against the clean-air colour.

**It leaves the locus itself — and by more than the first draft of this section claimed.** A monotone
power-law filter applied to a blackbody lands *near* another blackbody, because both are smooth and
monotone in wavelength, and against the original 1500 K calibration the composed colours sat only a
thousandth of a channel off the best-fitting Planckian point. After the refit to the 1000 K anchor the
column is three times deeper and the departure is 0.068 of a channel at the reference exponent — still
not a different family of colours, but no longer a rounding error either. What genuinely widens is the
**range of effective endpoints**: at a full sea-level load and a 20° sun the best-fit temperature runs
from 3257 K at α = 0 (the aerosol did nothing) down through 1188 K at the wettest tile's α = 2, where
§20b had one fixed endpoint for every map on every world. At α = 4 the composed colour is colder than
1000 K, i.e. colder than any temperature the Helland fit is valid for — that reference row now pins
against the search floor rather than against a fit, which is a stronger statement of the same claim.

**It cannot produce magenta, and no amount of α will change that.** `τ(λ) ∝ λ^-α` is *monotone* in
wavelength, so the three channels are always ordered by wavelength and green can never be attenuated
more than both of its neighbours. Magenta needs exactly that — blue lifted relative to green — which
requires a spectral **notch** rather than a slope. Magenta and purple twilights are real, but they are
**ozone**-driven: the Chappuis band absorbs 450–780 nm peaking at 603 nm, i.e. in the *middle*, which
is already modelled in §19. The ticket's "α ~2.0+ → salmon → magenta" row is wrong in that respect,
and the correction is pinned as a test (`NoExponentCanProduceMagenta_...`) rather than left as prose,
because the claim is plausible enough to be made again. Anyone chasing magenta should be reading
`OzoneTwilightMath`, not raising α here.

### What is pinned offline

- **α = 0 gives grey extinction.** The headline. Asserted on the *raw* transmission first (all three
  channels equal), because that is where the claim lives — asserting only the normalised form would be
  unfalsifiable. Then that the hue multiplier is exactly `(1, 1, 1)` and the composed sky colour is
  bit-identical to the clean-air colour, with a counterpart assertion that the reference exponent moves
  the same load a long way, so this cannot pass by the aerosol term being broken.
- **α = 4 reproduces Rayleigh's spectral shape**, twice over. Definitionally, as the per-channel
  optical depths standing in exact λ⁻⁴ ratios. And empirically, as the cross-check against §8's own
  curve the ticket asks for: fit the depth of a λ⁻⁴ filter to reproduce the *green* travel §8's Kelvin
  ramp performs from `ZenithKelvin` to `HorizonKelvin`, then check the *blue* travel it predicts with
  nothing tuned to it. It agrees to ~15%. The two models share no code and no constants, so that is
  real evidence rather than bookkeeping — and exact agreement would be suspicious, since they are
  different approximations of the same physics.
- **R/B transmission ratio rises monotonically with α**, swept in 0.05 steps across the whole band. If
  this failed the rainfall ramp would be mapping wetter tiles to arbitrary hues rather than to
  consistently redder ones.
- **Aerosol load 0 is bit-identical to §20's altitude-only curve**, as *exact* equality over a sweep of
  altitude × sun elevation × every named exponent. It holds by construction — at load 0 every optical
  depth is 0, every transmission is exactly `1.0f`, the normalising max is exactly `1.0f` — and
  asserting it exactly is what would catch someone adding an epsilon anywhere along that chain. Every
  tile in a game without Biotech takes this path.
- **The reference exponent reproduces §20b's shipped colour**, which is the subsumption pin: it says
  the retirement was a change of representation, not a retune. The blue residual is pinned too, since
  it is the one channel the calibration does not fix and therefore the one that reports a change in
  the model's shape.
- **§20b's own invariants, restated on colour rather than Kelvin** — the mountain-above-the-smog table,
  monotone in pollution at fixed altitude, monotone in altitude at fixed pollution, now additionally
  swept over exponent. One of them gets *sharper*: §20b needed a mired argument to show pollution's
  effect collapses ~14× with altitude rather than the ~3.8× its stacked Kelvin lerps suggested.
  Beer-Lambert composes additively in log space — the same property mireds have — so the 14.4× now
  falls out as arithmetic. Two models built for different reasons agreeing to two significant figures
  on a quantity neither was fitted to is the best evidence available offline that this is the same
  physics in a new representation.
- **§19 is invariant to everything aerosol**, asserted structurally the same way its altitude- and
  pollution-invariance are, with the filter extended to §20d's vocabulary. This guards a subtler
  mistake than the earlier versions did: §19 samples the *same three wavelengths* §20d does, and that
  shared basis is exactly what would make handing §19 an exponent look reasonable. `OzoneTwilightMath`
  is unmodified.
- **The exponent is clamped to [0, 4]**, with the floor mattering more than the ceiling: a negative α
  would attenuate blue *less* than red, silently reversing the sign of the whole effect.

### Compat and live verification

No new Harmony patch and no new vanilla member beyond `Tile.rainfall`, so the patch-order surface is
unchanged from §20b.

Two probes, because the halves fail independently: `aerosol_angstrom_exponent` (the input — what the
tile's rainfall resolved to) and `sky_red_blue_ratio` (the output — where the composed colour landed).
A desert tile reading 0.2 with an unchanged sky says the keying works and the spectrum is not being
applied; reading 1.3 says worldgen handed us a rainfall we did not expect. Deliberately **no** probe
reports the aerosol colour as a Kelvin: once the shape is applied the colour is not on the locus, so
any Kelvin would be a fiction. `sky_color_temperature` still reports the clean-air ramp and therefore
no longer moves with pollution at all — which reads like a probe regression and is the opposite, per
§18's rule that a probe must report the value its patch actually uses.

What cannot be checked offline: whether the α range reads as variety or as noise in play, and whether
the near-grey desert end reads as "dusty" or merely as "the effect stopped working". No slider ships,
for the reason §20 and §20b both declined one — that is a question for a live A/B, and adding a knob
before it is answered bakes in a guess.

**Measured.** `Tests/Scenarios/angstrom_rainfall.json` holds one sun, one full aerosol column and
`pollution = 1`, moving only the tile's rainfall — which is the single input α is keyed on. Perceptual
distance from the no-aerosol control, in CIELAB ΔE (CIE76) over every pixel of the rendered frame.
Median is the honest figure; the mean is dragged by a long outlier tail from UI text that does not
change between frames. Thresholds: <1 imperceptible, 1–2 close inspection, 2+ visible at a glance.

| tile | α | ΔE median, spectrum alone | ΔE median, amount-keyed boost | **ΔE median, as shipped** | R/B |
|---|---|---|---|---|---|
| arid 200 mm | 0.2 | 1.34 | 4.65 | **1.74** | 4.59 |
| temperate 1354 mm | 1.30 | 6.18 | 11.51 | **11.51** | 10.94 |
| rainforest 2500 mm | 2.00 | 7.29 | 14.23 | **14.23** | 12.35 |
| no aerosol (control) | — | — | — | — | 4.21 |

The three ΔE columns are one A/B/C, measured rather than argued. **Spectrum alone** is the model with
`AerosolBlendBoost` disabled — the floor, what the aerosol's own colour does on its own. **Amount-keyed
boost** is what §20b's boost produced before it learned about particle size. **As shipped** is with
`ChromaticFraction` applied.

Read the arid row. The grey case is this section's headline, and the amount-keyed boost was breaking it
on screen while the offline pin still passed: mean red *rose* 103.6 → 109.1 against the control, a full
grey column reading as warmer rather than dimmer. With the taper it reads ΔE 1.74 against a floor of
1.34, and mean red rises 0.42 instead of 5.51 — a thirteenfold reduction, and back below the ~2.0
"visible at a glance" threshold where a grey aerosol belongs.

The residual 0.42 is not a miss. α at the driest tile is `ThickDustExponent` = 0.2, not 0, and a 0.2
exponent genuinely is 14% as chromatic as urban haze — so a small warming is the honest answer rather
than an artefact. Driving it to exactly zero would mean moving the driest endpoint to the true grey
limit α = 0, which is inside the published 0.0–0.5 band for freshly lofted dust but is a separate
decision about what a desert sunset should look like, not about whether this composition is right.

Read the other two rows for the other half. **Identical to three decimal places**, because
`ChromaticFraction` clamps to exactly 1 from the reference exponent up. §20b's live result is not
re-litigated by this change; it is left alone everywhere §20b could actually see.

One consequence to state plainly, because it is a real cost and not a rounding error. §20b's motivation
was that a maximally polluted sky must be *visible at a glance*. On an **arid** tile it now is not —
ΔE 1.74, below that threshold. That is not this taper failing; it is §20d's own claim, applied
honestly. A large-particle aerosol does not redden a sky, so a colour-only lane has nothing to show for
it. The dimming it genuinely causes is the wavelength-flat half, which §8 deliberately does not write
and which is #78's to deliver through §9's saturation lane. Until then, maximum pollution on a desert
tile is close to invisible **by design**, and the alternative — warming a grey sky so the setting reads
as doing something — is the exact error `TintStrength` exists to refuse.

### Out of scope, filed separately

- **A weather-keyed exponent.** Fog and dust storms are the *literal* α ≈ 0 cases, and a `WeatherDef`
  read would reach them directly rather than through a tile's long-run rainfall. It is deliberately not
  done here: it would put a per-frame condition read in the hot path and it overlaps `WeatherDimming`'s
  lane, which this section does not touch.
- **A coastal sea-spray term.** Physically the cleanest route to α ≈ 0.2 on a wet map, and `Tile.IsCoastal`
  exists — but it calls `World.CoastDirectionAt`, which walks the tile's neighbours *and* pushes/pops
  global `Rand` state. Doing that inside `CurSkyTarget` would perturb the shared RNG every frame, which
  is not a cost worth a hue.
- **The §9 muting ticket** from §20b is unchanged and still keyed on the same `aerosolFraction`.
- **Pollution's effect on §7's night sky** and the **latitude-keyed ozone column for §19** are both
  unaffected by this section.

## 20e. Background aerosol — clean air is not aerosol-free (`AtmosphericColumn` / `SiteAltitude`, issue #92)

§20b keyed the whole aerosol column on `Tile.pollution`, and §20b, §20c and §20d all built on that one
fraction without questioning where it came from. `pollution = 0` is not a corner case: it is every
tile in a game without Biotech, and most tiles even with it installed, since worldgen only writes
pollution near industrial sites. On those tiles `aerosolFraction` was **exactly** 0 — not low, zero —
which made §20b's warming invisible (nothing to warm), §20c's drift invisible (`x × multiplier = 0` for
any multiplier), and §20d's hue shaping invisible (`τ · 0 = 0`, so every Ångström exponent produces an
identical, empty transmission). Three sections' worth of work sat behind a gate that was closed on
almost every map anyone actually plays.

**It is also physically wrong on its own terms.** Real clean air is never aerosol-free. Sea salt off
any body of water, wind-lofted dust, and biogenic organics from vegetation itself all contribute a
background aerosol optical depth (AOD) even over pristine wilderness — measured background values run
roughly 0.02–0.05 AOD at 550 nm for genuinely remote sites, rising to roughly 0.1 for an average
continental background away from any specific pollution source. §20b's own text already states a
heavy-urban AOD of "~0.3" when arguing for its calibration; those same published background ranges are
a fraction of that same anchor, not a new, unrelated scale to invent.

### Adding, not replacing

The fix sums a new background term with `pollution` **inside** `AerosolLoadFraction`, before either the
[0, 1] ceiling clamp or the altitude falloff:

```csharp
float seaLevelLoad = Clamp01(tilePollution) + Clamp01(backgroundLoadFraction);
return AerosolColumnFraction(siteAltitudeMetres) * Clamp01(seaLevelLoad);
```

This is the same choice §20b made when it decided pollution's colour effect enters the curve once
rather than twice (see §20b, "where it enters the curve"), generalised to a second source of the same
species. Pollution and background are not two different kinds of haze needing two different scale
heights or two different colour paths — they are both boundary-layer aerosol, indistinguishable to
Mie scattering, so they belong in one sum before either of the things that already act on the total
(the ceiling, the altitude column) rather than as a second parallel pipeline. That is also why this is
additive rather than a replacement of the zero-pollution case: `pollution` still means exactly what it
meant in §20b, a *human* contribution on top of the *natural* one, not a value that has to be
reinterpreted now that a second source exists.

Composing it this way for free inherits every property §20b already built and pinned: the same 1500 m
scale height, so a mountain sits above natural haze exactly as it sits above smog (`BackgroundIsSuppressedByAltitude_TheSameWayPollutionIs`,
mirroring §20b's own altitude table); the same ceiling clamp, so a tile that is both heavily polluted
and naturally hazy cannot exceed a full column; and the same drift multiplier from §20c, which is what
turns "every non-Biotech tile now has *something*" into "every non-Biotech tile now has something that
*varies*" — see §20c's updated text above for the honest limit on how much.

### Keying the background on rainfall — the "one lookup, two outputs" the issue asked for

The issue suggested a biome-keyed background. §20d had already solved the equivalent problem for the
Ångström exponent by keying on `Tile.rainfall` instead of a biome `defName` table, for a reason that
applies here without modification: vanilla's own `BiomeWorker`s score aridity from rainfall
(`BiomeWorker_ExtremeDesert` at the 340 mm cutoff, `BiomeWorker_TropicalRainforest` at the 2000 mm
cutoff), so rainfall *is* the axis a biome label is derived from, keyed continuously and with no table
for a modded biome to fall off the end of. Reusing `AerosolSpectrum`'s existing breakpoint constants
(`DriestRainfallMillimetres`, `WettestRainfallMillimetres`) rather than duplicating them means the same
rainfall read now drives both the aerosol *amount* (this section) and the aerosol *shape* (§20d) — one
live read off `Tile.rainfall`, two independent pure functions of it, which is what the issue's "share
one lookup" request actually wanted rather than a literal single accessor. `SiteAltitude`'s own header
comment states this explicitly (see the file's "three fields off one tile" note).

**Why a valley, not a straight line.** Both ends of the rainfall axis have a natural aerosol source,
just different ones: dry ground lofts mineral dust (α ≈ 0.2's own justification in §20d), while wet
ground supplies biogenic and sea-salt aerosol from denser vegetation and standing water. The rainfall
*midpoint*, not either extreme, is where neither source dominates:

```csharp
public static float BackgroundAerosolFraction(float rainfallMillimetres)
{
    float wetness = InverseLerpClamped(
        AerosolSpectrum.DriestRainfallMillimetres, AerosolSpectrum.WettestRainfallMillimetres,
        rainfallMillimetres);
    float distanceFromMidpoint = MathF.Abs(2f * wetness - 1f);
    return Lerp(PristineBackgroundFraction, ContinentalBackgroundFraction, distanceFromMidpoint);
}
```

`distanceFromMidpoint` is 0 at the rainfall midpoint and 1 at either breakpoint, clamped flat beyond
them the same way `InverseLerpClamped` already clamps §20d's exponent ramp — a tile drier than the
`ExtremeDesert` cutoff or wetter than the `TropicalRainforest` cutoff reads the same background as the
breakpoint itself, rather than extrapolating past where the physical reasoning still applies.

**Deriving the two endpoints rather than picking them.** `BackgroundAerosolAodAnchor = 0.3f` is §20b's
own stated heavy-urban AOD, reused rather than restated as a fresh constant. `PristineBackgroundAod =
0.035f` and `ContinentalBackgroundAod = 0.10f` are the midpoints of the two published ranges cited
above. Dividing each by the shared anchor gives the fractions the model actually uses:

| constant | AOD | ÷ 0.3 anchor | fraction |
|---|---|---|---|
| `PristineBackgroundFraction` | 0.035 | 0.035 / 0.3 | **0.1167** |
| `ContinentalBackgroundFraction` | 0.10 | 0.10 / 0.3 | **0.3333** |

This is the same move §20b made for its own constants throughout — a published or stated physical
figure divided by an existing in-codebase anchor, not a number chosen to make a screenshot look right.
It is honest about its own precision: "the midpoint of a cited range" is not a measurement, and the
comment on each constant says so rather than implying more confidence than a midpoint has.

### What is pinned offline

- **Never zero, for any rainfall.** The headline fix, asserted as a sweep of `BackgroundAerosolFraction`
  over 0–5000 mm (`BackgroundAerosolFraction_IsNeverZero`) and restated one level up as
  `AerosolLoadFraction(0, 0, background) > 0` for the same sweep at sea level
  (`EvenAtZeroPollution_TheSeaLevelColumnIsNeverAerosolFree`) — the second because that is the value
  every real consumer of this file actually reads, and a pin only on the private helper would not catch
  a regression introduced at the composition site.
- **The valley shape at its named points**, `[TestCase]`-pinned at the rainfall midpoint (lowest,
  `PristineBackgroundFraction`) and at both vanilla breakpoints plus 0 mm and 5000 mm (all four read
  `ContinentalBackgroundFraction`, the flat-clamped ceiling) — `BackgroundAerosolFraction_IsAValleyBetweenTheVanillaRainfallBreakpoints`.
- **Altitude suppression parity with pollution**, using the valley-rim (largest) background as the
  harder case: a 4000 m tile's background load is asserted under 10% of the sea-level value, the same
  order of magnitude §20b's own mountain-above-the-smog table shows
  (`BackgroundIsSuppressedByAltitude_TheSameWayPollutionIs`).
- **Additive composition and the shared ceiling**, asserted directly rather than inferred: pollution
  alone, background alone, and both together sum before the [0, 1] clamp, and a saturated case (both at
  their maximum) still clamps to exactly 1 rather than exceeding it
  (`CombinesPollutionAndBackground_SummedBeforeTheCeilingClamp`).
- **§20b's pollution-only claims survive with `background` pinned at 0.** The existing
  `AerosolLoadFraction_ScalesTheColumnByPollutionAndClampsIt` `[TestCase]`s were not retuned — they now
  pass `0f` explicitly for the new parameter, isolating the pollution half of the sum so a regression
  there cannot hide behind background covering for it.
- **§20b's monotonicity invariants, re-asserted under the shipped composition.** Both sweeps
  (`Warmth_IsMonotonicallyNonDecreasing_InPollution`,
  `Warmth_IsStillMonotonicallyNonIncreasing_InSiteAltitude_AtEveryPollutionLevel`) and the vacuum
  agreement (`BothColumnsReachZeroTogether_SoTheVacuumAgreementSurvivesTheSecondSpecies`) now run with a
  nonzero background (the valley-rim maximum) rather than 0 — proving the invariants against the case
  that actually ships, not just the pollution-only case §20b already covered.

### Compat

`Tile.rainfall` is the only new live read, and it is not actually new: §20d already reads it, and
`ApiCompatibilityTests.Tile_HasRainfall` already pins it. No new Harmony patch, no new vanilla member,
no DLC gate — the whole point of this section is that it must not need one.

`SiteAltitude.AerosolFractionForMap` grows the extra read and the extra call argument internally; its
signature and every caller are unchanged, which is what let this land without touching
`Patch_SkyColorTemperature` or either probe file's existing wiring at all.

### Live verification: run, mixed result

An earlier draft of this section claimed `RimWorldTestHarness` did not exist in this environment. That
was wrong — a stale conclusion carried over from an earlier search that looked in the wrong place. The
harness lives at the sibling repo `RimWorldTestHarness` and builds and runs cleanly; the correction is
left here rather than silently edited away, per this mod's own honesty bar.

The scenario (`Tests/Scenarios/background_aerosol_clean_air.json`) puts four `pollution = 0` tiles in
front of the camera at dusk — the rainfall midpoint (lowest background, `PristineBackgroundFraction`),
both vanilla aridity breakpoints (340 mm and 2000 mm, both reading `ContinentalBackgroundFraction`), and
a 4000 m mountain at the arid breakpoint (background suppressed by the same altitude column §20b already
uses) — and screenshots each. "Before" is the main checkout's currently-shipped (pre-#92) build with no
overlay; "after" is `--mod-overlay` onto this worktree's build, both against the same save fixture, same
camera, same time of day. ΔE is CIELAB CIE76, computed per-pixel and reported as the **median** across
the frame (not the mean, which a few unrelated HUD/text pixels near ΔE 80+ drag upward) — the task's own
judging criterion.

**With Biotech installed** (the default-owned-DLC case):

| tile | background fraction | median ΔE |
|---|---|---|
| rainfall midpoint (1170 mm) | 0.117 (Pristine) | **1.346** |
| arid breakpoint (340 mm) | 0.333 (Continental) | **0.662** |
| wet breakpoint (2000 mm) | 0.333 (Continental) | **5.500** |
| 4000 m mountain (340 mm) | 0.333 × altitude falloff ≈ 0.022 | **1.865** |

Three of four tiles clear the ΔE ≥ 1 "imperceptible" floor this task set as the line for "not yet
shipped," and the wet breakpoint clears ΔE ≥ 5 ("obvious"). The arid breakpoint is the one exception:
0.662, under the imperceptible threshold, **despite having the same background fraction as the wet
breakpoint that scored 5.500.** Both tiles carry the identical `ContinentalBackgroundFraction` load —
the gap between them is not explained by the aerosol math, and is far more likely dusk-lighting geometry
(sun angle/scatter interacting differently with each tile's specific latitude/season combination) or
ordinary frame-to-frame render noise (clouds, pawn animation, particle effects) than a defect in
`BackgroundAerosolFraction`'s valley shape, which the offline pins already constrain tightly. Reported
plainly rather than averaged away: **this section is visible at a glance on 3 of 4 sampled tiles, and
only borderline-imperceptible on one, with no identified defect explaining the outlier.** Per this
task's own rule, a result this mixed is not waved through as a clean pass — see the "not yet fully
shippable" framing below.

**Without Biotech installed** (`--without-dlc ludeon.rimworld.biotech`), same scenario, same camera:

| tile | median ΔE (Biotech) | median ΔE (no Biotech) |
|---|---|---|
| rainfall midpoint (1170 mm) | 1.346 | **1.347** |
| arid breakpoint (340 mm) | 0.662 | **0.663** |
| wet breakpoint (2000 mm) | 5.500 | **5.500** |
| 4000 m mountain (340 mm) | 1.865 | **1.844** |

The probe read the identical `aerosol_load_fraction` values with Biotech absent (0.1199, 0.3426, 0.3426,
0.0238 — bit-for-bit the same four numbers as the Biotech run) and the ΔE numbers land within run-to-run
noise of each other. This is the result issue #92 asked this section to produce: the fix is not gated on
Biotech anywhere in `AtmosphericColumn`/`SiteAltitude`, because `Tile.pollution` and `Tile.rainfall` are
both plain fields read the same way regardless of which DLCs are installed, and the numbers confirm that
holds at the live-render level too, not just in the source.

### Not yet fully shippable, honestly reported

Per this task's own bar: a result under ΔE 1 is not claimed as a success. The arid-breakpoint tile scored
0.662 — under that floor — even though its background aerosol load is identical to the wet-breakpoint
tile that scored 5.500. That inconsistency, not the aerosol math itself (which the extensive offline
pins above already constrain), is the open question. Two most likely explanations, neither chased down
in this PR: (a) genuine live-render noise between separate game-boot runs (clouds/pawns/particles differ
frame-to-frame in ways the offline pins cannot see), or (b) the specific dusk sun angle at the arid
tile's latitude/season combination scattering the added haze into a colour close enough to the
unmodified sky that CIELAB distance happens to be small at that one geometry, independent of the
aerosol fraction itself. The Biotech/no-Biotech pair above is weak evidence for (b) over (a): the
arid-breakpoint ΔE reproduced to three significant figures (0.662 vs 0.663) across two independent game
boots, which argues against pure frame-to-frame render noise and toward something deterministic about
that tile's geometry — but two data points sharing every input except the DLC flag is not the same as
the second-time-of-day check that would actually distinguish them. This section ships as offline-correct
and measured, not as a clean unconditional pass — the follow-up is to re-run this same scenario at a
second time-of-day to see whether the arid-breakpoint result is geometry-dependent or reproducible.

**RESOLVED (issue #111).** Neither (a) nor (b) above. The eightfold gap is real, deterministic, and
already fully explained by code this section's own text cites two paragraphs up but does not connect
back to the anomaly: **`AerosolLoadFraction` is not the only rainfall-keyed input the sky patch reads.**
§20d's `AerosolSpectrum.AngstromExponentForRainfall` is a second, independent function of the same
`Tile.rainfall`, keyed on the same two vanilla breakpoints but shaped as a **monotonic ramp** rather
than this section's valley. At the arid breakpoint (340 mm) it reads `ThickDustExponent` (0.2 — near-grey
extinction, the "sun dims without shifting hue" case §20d's header names explicitly). At the wet
breakpoint (2000 mm) it reads `FineSmokeExponent` (2.0 — strongly wavelength-selective extinction, the
"deep saturated red" case). Both breakpoints share the identical `AerosolLoadFraction`
(`ContinentalBackgroundFraction`, confirmed live: the probe read 0.3426 at both), but they do **not**
share a colour — and CIELAB ΔE weighs hue shift heavily, so identical *amount* at wildly different
*colour* produces exactly the kind of lopsided spread measured here.

This is not a new hypothesis requiring fresh live capture: `AerosolSpectrum.ChromaticFraction` — already
pinned offline (`ChromaticFraction_IsZeroAtGreyAndSaturatedAtTheReferenceExponent`) before this section
ever shipped — already states the ratio. `ChromaticFraction(ThickDustExponent)` = 0.1437 vs
`ChromaticFraction(FineSmokeExponent)` = 1.0 (clamped): a ~6.96× spread, the same order of magnitude and
the same direction as the measured ~8.31× live ΔE ratio (5.500 / 0.662). Re-deriving the committed
`Tests/Screenshots/background_*` pairs independently (median CIELAB ΔE, per-pixel, no game boot required)
reproduces the table exactly and shows the mechanism directly in the mean frame colour: arid moves
rgb(104,61,25) → rgb(104,60,24) (a near-flat shift — grey extinction, as `ThickDustExponent` predicts),
while wet moves rgb(104,61,25) → rgb(106,56,16) (a large, blue-channel-crushing shift — strongly
selective extinction, as `FineSmokeExponent` predicts).

The three other candidate explanations issue #111 raised are each ruled out by evidence, not by
assumption: **terrain dependence** is impossible by construction — `SetTilePropertiesAction` (the
harness step this scenario's four "tiles" are built from) writes only `Tile.elevation` / `.pollution` /
`.rainfall` on the live `Tile`; it never touches the map's terrain grid, so all four captures share one
physical map, camera and terrain, and a whole-frame ΔE cannot be terrain-biased between them. **Mismatched
solar elevation** is ruled out by the four "before" screenshots themselves: three of the four are
byte-identical and the fourth differs by a median ΔE of exactly 0.00 against them (confined to 2.3% of
sampled pixels, consistent with ordinary transient render noise from being first in the capture
sequence) — which is exactly what the pre-#92 math predicts (pollution = 0 ⇒ `AerosolLoadFraction` = 0
regardless of altitude or rainfall) and confirms all four captures share one solar geometry, not four.
**The overlay silently not loading** (a failure mode that has bitten this project before) is ruled out by
the run logs: both the baseline and overlay runs list `joof.celestiallighting` in the 10-mod `ModsConfig`
that was actually written, and the overlay run additionally logs "all 1 overlay target(s) are active in
run's modlist" before launch — the exact check that would have failed had the base `--mod` been omitted.
The mountain tile's own ΔE (1.865, higher than arid's 0.662 despite a much smaller aerosol amount there,
~0.022 after altitude falloff) is left unexplained by this section and is very likely §20's independent
site-altitude curve, not aerosol at all — filed as a loose end rather than chased here, since it is
outside what issue #111 asked.

Pinned offline as `AridAndWetRainfallBreakpoints_ShareAerosolAmount_ButNotAerosolColour` in
`SkyColorTemperatureTests.cs`: the amount is asserted identical at both breakpoints (guarding the
premise), the exponents are asserted to be the two named constants (guarding the mechanism), and the
`ChromaticFraction` ratio is asserted to stay above 5× (guarding the magnitude) — so a future change that
quietly re-aligned the two ramps' colours, and silently made this section's numbers stop meaning what
they say here, fails loudly instead of waiting for the next live A/B to notice.

**What this means for the reference ΔE scale.** The scale is trustworthy for the specific tile/config
each number was measured at — 0.662 is a correct measurement of "aerosol amount alone, at
`ThickDustExponent`, is nearly imperceptible", not evidence the amount model is broken or invisible in
general. Issue #111's "why it matters" point stands on its own regardless of this resolution, though:
a single ΔE-per-subsystem number *is* an oversimplification once two independently-keyed inputs (amount
and colour) both vary with the same live quantity, and a scenario that probes only `aerosol_load_fraction`
— as this one does — cannot see that the colour half moved at all. Extending
`background_aerosol_clean_air.json` to also `Probe aerosol_angstrom_exponent` at each tile is a natural
follow-up, filed rather than done blind here since pinning a live scenario's expected values needs its
own live run to derive them, which this investigation did not require.

### Out of scope, filed separately

- **§20c's `DriftAmplitude` retune.** Filed as `Jeffrharr/CelestialLighting#108` (referenced from §20c's
  own "out of scope" list above too): `amplitude = 0.35` was sized against a full pollution column, and the
  background load this section adds is smaller than that column everywhere on the valley-shaped curve
  (`ContinentalBackgroundFraction` = 0.333 vs. a full-pollution column of 1.0), so the same amplitude
  produces a proportionally smaller day-to-day swing on a non-Biotech tile. Target: retune so the median
  ΔE lands in the 1–2 "close inspection" band on a representative non-Biotech tile, using the same
  harness/methodology this section used. Not retuned blind in this PR — it needs its own live A/B once
  this section's baseline load is live to retune against.
- **A settings slider**, declined for the reason every prior aerosol section declined one: this is a
  question for a live A/B, and a knob added before that answer exists bakes in a guess. This section's
  now-measured ΔE (mostly 1–5.5, one outlier at 0.66) argues against urgency here — it is visible enough
  on 3 of 4 sampled tiles that a slider is not filling a gap the fix left open.
- **The arid-breakpoint ΔE anomaly's root cause** (0.662 vs. the wet breakpoint's 5.500 despite an
  identical background fraction) — see "Not yet fully shippable" above. Needs a second time-of-day
  re-run to distinguish render noise from tile-geometry dependence.
- ~~A live probe for the raw background/load input~~ — done, not filed: `aerosol_load_fraction`
  (`Source/Probes/AerosolLoadProbe.cs`) landed in this same PR and is what produced every number in this
  section. Left here, struck through, only so a reader scanning this list does not go looking for a gap
  that has already been closed.

## 21. Snow albedo: the surface-cloud light cavity (`AlbedoCavityMath` / `SurfaceBuildup`)

**Problem.** Fresh snow under a thick overcast is far brighter than the same overcast over bare
ground — and, counterintuitively, brighter *in diffuse light* than a **clear** sky over that same
snow. Nothing in the mod models it, and the naive fix ("snowy maps are brighter") gets the
interesting half exactly backwards.

**The physics — a light-trapping cavity, not a bright surface.** Two ingredients:

1. **Snow albedo is enormous.** Fresh snow reflects 0.80–0.90 of incident shortwave; settled or
   melting snow 0.40–0.60; soil, rock and vegetation 0.10–0.25. Snow swings the surface reflectance
   by roughly a factor of four.
2. **That reflected light is returned by the cloud base.** Light bounces snow → cloud → snow →
   cloud, a geometric series converging to

   ```
   A = 1 / (1 - a_surface * a_cloud)
   ```

| surface | overhead | A | gain vs bare ground |
|---|---|---|---|
| fresh snow (0.85) | thick overcast (0.75) | 2.76 | **2.34×** |
| settled snow (0.50) | thick overcast (0.75) | 1.60 | 1.36× |
| sand (0.35) | thick overcast (0.75) | 1.36 | 1.15× |
| bare ground (0.20) | thick overcast (0.75) | 1.18 | 1.00× (baseline) |
| fresh snow (0.85) | clear sky (~0.10 Rayleigh backscatter) | 1.09 | 1.07× |
| bare ground (0.20) | clear sky | 1.02 | 1.00× |

Snow buys ~2.3× under a deck and **7% under a clear sky**, because a clear sky is not a reflector.
That inversion is the whole subsystem: it is why snowy overcast feels wrong, and why the effect
cannot be had by making snowy maps brighter.

### Why the shipped quantity is a ratio, not `A`

`A` over bare ground is *already* 1.18 under an overcast. The cavity does not switch on when it
snows; it merely gets much stronger. Every brightness anchor this mod ships — §6b's lux table, §7's
starlight/airglow floors — was read off published measurements taken over ordinary ground, so the
bare-ground cavity is already inside them. Applying raw `A` would double-count it and brighten a
mud-and-grass map that nothing had changed.

`AlbedoCavityMath.CavityGain` therefore divides by the bare-ground baseline. That makes "no buildup"
**exactly 1.0** — bit-identical to pre-§21 behaviour, with no epsilon and nothing tuned, because the
numerator and denominator are literally the same expression. `AlbedoCavityMathTests` leads with that
pin and asserts it as an exact equality across the whole cloud range; if it ever needs a tolerance,
the ratio has been replaced by something else.

### Keyed on albedo, not on snow depth

RimWorld 1.6 generalized snow into **weather buildup**: `SnowGrid.GetCategory` returns a
`WeatherBuildupCategory` via `WeatherBuildupUtility`, and Odyssey ships `Map.sandGrid` — a
byte-for-byte sibling of `SnowGrid` (same `NativeArray<float>` depth grid, same maintained
`totalDepth` accumulator, same `MaxDepth = 1f`). Sand is not white but it is not bare ground either
(~0.35 against soil's ~0.20), so it falls out of the same formula with a different constant.

The pure core therefore takes an **albedo**, never a snow depth, and `BuildupSurfaceAlbedo` takes its
three albedos as arguments rather than reading the constants. The sand arm is one adapter read plus
`AlbedoCavityMath.SandAlbedo`, with no new maths in the ramp itself — pinned structurally by
`BuildupSurfaceAlbedo_TakesItsAlbedosAsArguments_SoSandNeedsNoSecondRamp`. Sand has no "settling"
story the way snow does, so its call passes `BareGroundAlbedo` for both the bare and shallow
arguments — flattening the optical-cover segment to a no-op and leaving a single ramp straight from
bare ground to `SandAlbedo` above `ShallowBuildupDepth`.

**One correction to the framing this landed with:** sand is *not* the same grid reached through
`WeatherBuildupUtility`. Both grids route their *categories* through
`WeatherBuildupUtility.GetBuildupCategory`, but the *depths* live in two separate grids, so the sand
arm reads `map.sandGrid` rather than a category off the snow grid.
`ApiCompatibilityTests.Map_HasSandGrid_ShapedLikeSnowGrid` records that.

**Combining the two covers.** `SurfaceBuildup.CavityGainFor` reads both grids independently — each
map keeps a `snowGrid` and a `sandGrid` at once, whether or not the biome ever fills the other — and
composes their two ramped albedos with `AlbedoCavityMath.CombinedSurfaceAlbedo`, which takes the
**max**, not a sum. A cell's ground is buried under snow, or showing sand, or bare; never a stack of
both depths at once, so the map's true areal-mean albedo is bounded above by whichever single cover
is currently more optically dominant. Max is also the identity on every map that only ever reads
nonzero on one grid — everything that shipped before this — because the other argument is always
exactly `BareGroundAlbedo`, and each ramp's own floor is `BareGroundAlbedo` by construction.
`CombinedSurfaceAlbedo_IsTheIdentity_WhenOnlyOneCoverIsPresent` pins that.

### Depth → albedo: two segments, two different physical facts

`BuildupSurfaceAlbedo` ramps in two pieces rather than one, because conflating them would make a
half-thawed map either too bright or too dull:

| depth | what is happening |
|---|---|
| `0 → 0.25` | **Optical cover.** A dusting does not have snow's albedo, it has a mixture of snow's and the dirt showing through. |
| `0.25 → 1` | **Fresh versus settled.** Once the ground is hidden, the remaining variation is the snowpack's age. |

Depth is a fair proxy for age *in RimWorld specifically*: `SnowGrid` melts depth down over time, so a
deep grid **is** a recent fall and a shallow one **is** a settling pack. That is a happy accident of
the vanilla simulation rather than a general truth, and it is what lets this ramp be honest with one
input instead of needing an age we would have to track ourselves.

The knee at `0.25` is **vanilla's own** `Dusting`/`Thin` boundary from
`WeatherBuildupUtility.GetBuildupCategory`, not a number of ours — "you can no longer see the dirt"
is the same threshold in both models, so the two agree by construction. `ApiCompatibilityTests`
reads the IL literal so a Ludeon retune shows up as a disagreement rather than a drift.

### Where it lands: §7's night floor

`SurfaceBuildup.CavityGainFor(map)` multiplies the floor inside `NightRadiance.FloorGlowFor`, not
inside `Patch_NightRadiance`. The gain belongs to "how dark can the sky over this map get", which is
the question that file exists to answer for all three of its consumers (§7's blend, §18c's umbra
floor, §18e's eclipse minimum) — so they cannot disagree about it. The two vacuum consumers cost
nothing: the gain is exactly 1 on a vacuum map.

This is the interaction the subsystem was worth building for. §7 delivers *true pitch-black nights*,
and a **snowed-in map should not go pitch black** — a full moon on fresh snow under cloud is famously
bright enough to read by. Before §21 it did. The tension is resolved in favour of the physics, and it
is resolved by the map's own state rather than by a special case.

§9 Purkinje then consumes the raised brightness **for free**, and correctly: §9 keys its rod-vision
ramp on apparent brightness, and snow is achromatic — it shifts no hue while it desaturates less. §21
therefore writes **no saturation term of its own**. A second desaturation input would have been a
second source of truth for a number §9 already owns.

### Issue #100: on Clear weather, reading §22's continuous fraction instead of §13's abstention

The night floor's `a_cloud` comes from `WeatherDimming.CloudOpacityFor` — §13's classifier — for
every weather **except** Clear. §13 was built to classify precipitation, and it scores Clear as
opacity exactly 0 by construction (`WeatherDimmingMath` has no term for it on either axis). Before
§22 existed that 0 was accurate: a Clear sky had no notion of partial cover at all. Once §22 shipped
a continuous, hourly-drifting cloud-cover fraction for exactly this one weather
(`CloudCoverClock.FractionForMap`, §22 above), §13's 0 stopped being "no cloud" and became "no
opinion" — the night floor kept reading a literal clear sky while §22 was already rendering that same
sky as up to a third overcast, and a snowed-in colony under a hazy Clear night got none of this
subsystem's amplification.

`AlbedoCavityMath.EffectiveCloudOpacity` closes that gap: `SurfaceBuildup`'s private
`CloudOpacityOrClear` reads the map's current weather once, and substitutes §22's fraction for §13's
reading only while that weather is actually Clear. Gated on the weather being Clear, not on
`weatherOpacity == 0` — §13 can also read 0 because the feature is off or because the map has no sky
at all (caves, pocket maps), and neither of those means "this map is in Clear weather right now";
conflating them would leak §22's drift into places that have no business seeing a tile's weather.

This was a **narrower** fix than it might sound, and it stayed narrow for two releases: only the
one-arg `CavityGainFor(Map map)` — the night floor's own entry point — changed. The two-arg overload
`WeatherDimming.DimmingFor` threads through short-circuited to zero dimming before ever reaching the
cavity on true Clear weather, so it was never reading the stale 0 in the first place, and §22 already
tints the Clear sky's daytime colour directly (`Patch_CloudCoverSky`).

**Issue #134 later closed the day/night asymmetry that left behind, and it is worth being precise
about what changed.** The *dimming* is still §13's alone and is still exactly 0 on Clear — deriving a
second dimming from §22's fraction would darken a partly-cloudy day twice, once here and once in
`Patch_CloudCoverSky`, which is why that was rejected then and is still rejected now. What changed is
that the daytime read now asks the same "is there a deck overhead" question the night arm asks
(`WeatherDimming.DeckOpacityFor`, through this same `EffectiveCloudOpacity`), so the cavity gain is no
longer a different number either side of dusk. Nothing on this page renders differently as a result —
`RecoveredDimming` clamps a zero-dimming cavity away exactly as before — and the whole of the
recovered amplification goes to §24's additive lane instead. See §24.

**The off-fallback is exact by construction, not by a second branch.** `CloudCoverClock.FractionForMap`
returns exactly 0 when `CelestialLightingFeatures.CloudCover` is off — the same 0 §13 already reports
for Clear — so with §22 off this reproduces the pre-#100 reading bit-for-bit with no dedicated
fallback path to keep in sync. `AlbedoCavityMathTests` pins this explicitly
(`EffectiveCloudOpacity_FeatureOff_FallsBackToTheExactPreIssue100Reading`).

**Live verification.** `Tests/Scenarios/cloud_cover_albedo_cavity.json`, latitude 45, hour 2 (sun
well below the horizon, so the reading is genuinely the night floor and not daytime dimming). Day 52
was chosen after a moon-phase survey specifically for a near-full moon (`moon_illumination` 0.9924):
the floor this gain multiplies is starlight + airglow + moonlight, so a fuller moon gives the
multiplier more absolute glow to amplify — the same tile/snow/hour at day 40's dimmer moon phase
measured a correct but imperceptible median ΔE of 0.83. With `cloud_cover` off, `cavity_gain` reads
`1.0316` (the discrete Clear-sky backscatter, matching pre-#100 behaviour bit for bit). With it on,
`cloud_cover_fraction` reads `0.3556` and `cavity_gain` follows it up to `1.1192`. Median CIELAB ΔE
**1.12** — visible on close inspection, at the low end of the measured set (§20c aerosol drift 0.36,
§19b ozone column 1.48, §20 site altitude 1.88) — consistent with amplifying a floor that starlight
and airglow already keep small even at full moon.

### The daytime half: giving §13's dimming back over snow

The reported effect is a **daytime** one — "brighter with snow on the ground", i.e. snow glare under
an overcast. The night floor above does not deliver it, so §21 has a second consumer:
`WeatherDimming.DimmingFor` composes the cavity gain into §13's dimming before anyone reads it.

**Why this is not a sign error, which is the first thing a reader will suspect.** §13 darkens for
cloud; §21 brightens for the same cloud. Both are correct and they are not the same effect:

| | what the deck does | who models it |
|---|---|---|
| blocks the incoming direct beam | ground gets less light | §13's dimming |
| reflects from its own base | the light already down there comes back | §21's cavity |

A cloud does both, for the same reason it is a cloud. Over **bare ground** the blocking wins outright
— the gain is exactly 1, the composition is the identity, and §13 is untouched. Over **fresh snow**
there is so much light going back up that the deck returns more than it withheld.

The composition is exact and introduces no constant:

```
surviving = (1 - dimming) * cavityGain
dimming'  = max(0, 1 - surviving)
```

| surface, weather | §13 dimming | gain | recovered dimming | rendered tint |
|---|---|---|---|---|
| bare ground, dry overcast | 0.180 | 1.000 | 0.180 | 0.820 |
| sand, dry overcast | 0.180 | 1.153 | 0.055 | 0.945 |
| settled snow, dry overcast | 0.180 | 1.360 | 0.000 | 1.000 |
| fresh snow, dry overcast | 0.180 | 2.344 | 0.000 | 1.000 |
| fresh snow, blizzard | 0.255 | 2.344 | 0.000 | 1.000 |
| fresh snow, clear sky | 0.000 | 1.071 | 0.000 | 1.000 |

**The clamp at zero is the renderer's ceiling, not a taste call.** `SkyColorSet.sky` is assigned
straight to `MatBases.LightOverlay.color`, a **multiply**, and vanilla's brightest palette is Clear's
`(1,1,1)` — which already means "do not darken this scene at all". There is no headroom above it. A
negative dimming would ask that material to brighten, which it cannot express; that would need an
additive pass, i.e. a real glare/bloom feature. So the honest ceiling for the daytime half is *a
snowy overcast renders as bright as a clear day*, which is exactly the reported phenomenon — an
overcast that refuses to feel dim. Before §21 it rendered at §13's full 18% darkening, visually
indistinguishable from the same overcast over mud.

The physically larger claim — snowy overcast *brighter* than snowy clear sky — is therefore
expressible only in the unclamped diffuse model (pinned in the tests) and on §7's night floor, which
has headroom where the daytime colour channel does not. Snow-glare bloom is the follow-up that would
close that gap and is not attempted here.

**Which consumers it reaches, and which it deliberately does not.** `DimmingFor` is the shared read,
and all three of its consumers want the recovered value: §13's sky tint, §9's `ApparentGlow`, and
§9's per-cell night-wash strength. A snowy overcast that renders brighter must also desaturate less,
and it does so here for free — which is why §21 writes no saturation term of its own.

`Patch_ShadowStrength` is **not** reached, because it reads `CloudOpacityFor` rather than
`DimmingFor`. The deck still softens shadows by the full amount however much diffuse light is
bouncing around. That asymmetry is the physics rather than a missed call site: the cavity restores
**brightness** and destroys **contrast**, and together those are the whiteout that makes terrain hard
to read in snow. A test pins it so it reads as intent.

**One implementation note that cost a test failure and is worth keeping.** `RecoveredDimming`
early-returns the input when the gain is 1, rather than letting the algebra do it. `1 - (1 - d) * 1`
is `d` in real arithmetic and *not* in IEEE 754 single precision — `d = 0.2175` round-trips to
`0.21749997`. This value reaches every map on every save, so "bare ground is bit-identical" should be
exact or should not be claimed. The offline test asserted exact equality, caught it, and the fix is a
branch rather than a tolerance.

### Composition with §13 on the night channel, and what is deliberately not modelled

At night the two subsystems stay on separate channels: §21 lifts `SkyTarget.glow`, §13 dims
`colors.sky`/`.overlay`, and their **product** is what reaches the screen.

| | gain (§21) | tint (§13) | composed |
|---|---|---|---|
| fresh snow, dry overcast | 2.344 | 0.820 | **1.923** |
| fresh snow, blizzard | 2.344 | 0.745 | 1.747 |
| fresh snow, clear sky | 1.071 | 1.000 | 1.071 |
| bare ground, dry overcast | 1.000 | 0.820 | 0.820 |
| bare ground, clear sky | 1.000 | 1.000 | 1.000 |

The headline inversion test is stated in those composed terms, not in raw gain, because a test on
gain alone would assert something the player never sees and would pass even if §13's dimming ate the
whole effect. The bare-ground row is the control: with no buildup the cavity gain is exactly 1, the
dimming is unopposed, and an overcast is simply darker than a clear sky — which is what the mod did
before §21 and must keep doing. Without that case the inversion test would pass just as well from a
bug that brightened every overcast map.

**What is deliberately NOT modelled: cloud extinction of the night sources.** A thick deck both
closes the cavity (modelled) and attenuates the starlight and moonlight arriving through it (modelled
nowhere — §13 owns weather attenuation and owns it on the colour channel, never on `.glow`). So the
amplified night floor is an **upper bound** on a snowy overcast night. That is a consequence of §13's
channel split rather than an oversight, and it is the first thing the live A/B should look at.

### Gameplay scope: `.glow` is not a free channel

§7 writes `SkyTarget.glow`, which **is** gameplay-visible — `GlowGrid.GroundGlowAt` feeds
`PlantProperties.growMinGlow` (0.51), `CompPowerPlantSolar` and pawn psych-glow. Amplifying that
floor could in principle make crops grow at night on a snowy map. It does not: the brightest
reachable floor (starlight 0.02 + airglow 0.02 + a full moon 0.15 = 0.19) times the largest reachable
gain (2.344) is **0.446**, comfortably under 0.51.

That is a bound the shipped *constants* give us, not one the code enforces — raising
`MaxMoonlightGlow` or the snow albedo far enough would cross it silently, and the symptom in play
would be crops growing in the dark rather than anything that looks like a lighting bug. So it is
pinned (`AmplifiedGlow_StaysBelowThePlantGrowthThreshold_OnTheDefaultFloor`) rather than left to be
noticed.

#### Scoped out: amplifying daylight `.glow`

The obvious way to build the daytime half would have been to amplify `.glow` in daylight the way §21
amplifies it at night. It was analysed and rejected, and the analysis is worth keeping because the
conclusion is not obvious from either end.

Vanilla's glow is `GenCelestial.CelestialSunGlowPercent`, which returns

```csharp
Mathf.Clamp01(Mathf.InverseLerp(0f, 0.7f, Vector3.Dot(surfaceNormal, sunPosition)))
```

— a **clamped 0..1 quantity that peaks at exactly 1.0**. That single fact splits the day in two, and
both halves are bad:

- **At noon, where the effect should be strongest, it is a no-op.** Glow is already 1.0. `1.0 × 2.344`
  clamps straight back to 1.0. A player standing on fresh snow at midday would see nothing.
- **At the shoulders, where it is not a no-op, it is a gameplay change.** At glow 0.25 (mid-morning),
  `× 2.344` is 0.586. That crosses `PlantProperties.growMinGlow` (0.51), lengthening the outdoor
  growing window on every snowy map. It also lands in vanilla's `0.6` band, which is not merely a
  plant threshold — `GenCelestial.CurShadowStrength` is `Abs(glow - 0.6) / 0.15`, `IsDaytime` keys on
  it, and `GetLightSourceInfo` uses it to hand over between `LightingSun` and `LightingMoon`. Raising
  glow across it would shift the sun/moon light-source switchover and the shadow-strength curve on
  every snowed-in map.

So the glow lane offers *no effect where the phenomenon lives* and *a real, uninvited gameplay change
where it does not* — precisely the trade §13's design section refused in the opposite direction. The
colour lane has the inverse profile: it is where the darkening the player currently sees is applied,
it is bounded by vanilla's own palette maximum, and it costs nothing gameplay-visible. That is why
the daytime half went there.

Two consequences to state plainly rather than leave implied. **`.glow` in daylight is bit-for-bit
vanilla under §21** — plant growth, solar output and pawn vision are untouched by the daytime arm, and
`AmplifiedGlow` is reached only from `NightRadiance.FloorGlowFor`. And **the daytime effect is
therefore purely visual**, which is the correct scope for this mod but does mean a snowy overcast is
brighter to look at without being brighter to a solar panel. Anyone who later decides the physical
claim should reach gameplay is opening a different ticket with a different risk profile, and this
section is the starting point for it.

### Cost: whole-map, and why the per-cell half is deferred

`map.snowGrid.TotalDepth / map.Area` is **O(1)**. Decompiling `Verse.SnowGrid` against 1.6's
`Assembly-CSharp` shows a `private double totalDepth` incremented inside `AddDepth`/`SetDepth` and
exposed as `public float TotalDepth => (float)totalDepth` — a maintained running total, not a grid
scan. `SnowGrid.MaxDepth = 1f`, so dividing by `Map.Area` (`Size.x * Size.z`) gives a mean depth
already normalized. `ApiCompatibilityTests.SnowGrid_HasTotalDepth_AsAMaintainedRunningTotal` asserts
the private accumulator still exists, because that is the cheapest available proxy for "this is still
free" — a reimplementation as a loop would break nothing, error nowhere, and quietly start scanning
the map twice a frame.

A whole-map average is also the **right** model for an ambient term, not merely the cheap one. The
cavity is a multi-bounce integral over everything the cloud base can see, which at cloud-base height
is most of the map — so a cell standing on bare mud in the middle of a snowfield genuinely is lifted
by its neighbours. Averaging over `Map.Area` rather than over snow-capable cells is part of that:
roofed cells, water and building footprints hold no snow and correctly dilute the mean.

**DEFERRED: the per-cell shadow-fill half.** §18c owns "what fills a shadow" — skylight at sea level,
the night budget in vacuum — and snow is the third term and the strongest of the three. The cavity
also makes illumination near-isotropic, so shadows flatten and directional shading dies: the whiteout
that makes terrain hard to read in snow. That is a *contrast* effect, not just a brightness one, and
`SnowGrid.GetDepth(cell)` exists to drive it per cell (pinned by
`SnowGrid_HasGetDepth_ForTheDeferredPerCellHalf`, which is a standing check that the option is still
available rather than a test of anything we call).

It is deferred on §16's ledger, not on taste. §16 measured what one dirty flag costs across §7b/§9/§15
(issues #20 and #60), and a per-cell snow term would either need its own `SectionLayer` subscribing to
`MapMeshFlagDefOf.Snow` — a flag that is raised constantly during snowfall, by
`SnowGrid.CheckVisualOrPathCostChange` on every cell that crosses a category boundary — or a per-cell
read inside an existing layer's vertex loop. Both are exactly the shape §16 says to cost before
building. The ambient half needed none of that and is worth proving in play first.

**Shipped: the sand arm.** `SurfaceBuildup.CavityGainFor` reads `map.sandGrid` alongside `snowGrid`
and composes the two ramps with `AlbedoCavityMath.CombinedSurfaceAlbedo` (max, not a sum — see
"Combining the two covers" above). It landed after the snow half's ambient gate was already proven
in play; a desert map with dune buildup was the live A/B this addition owed, and it has now been run
(`sand_albedo_cavity.json`) — see the outstanding-note bullet below for the measured numbers.

A first attempt at that scenario painted a single 128×128 `SetSand` patch (the harness's per-call
cell cap) on the fixture's 250×250 map and measured **ΔE 0.00 at every tested condition** — not a
weak effect but an invisible one, and different enough from snow's result under the identical patch
size (ΔE 6.06) to treat as suspicious rather than simply "the sand arm is subtler." A
`surface_cavity_gain` probe (`SurfaceBuildup.CavityGainFor` read straight off the live map) confirmed
it: gain measured `1.0000931`, essentially the no-op floor. The cause is the flat first ramp segment
sand deliberately uses (see "sand has no settling story" above) meeting `MeanSandDepth`'s whole-map
areal average (see "why mean depth and not a per-cell read" above): one 128×128 patch on a 250×250
map dilutes to a mean depth of ≈0.262, only *just* past `ShallowBuildupDepth` (0.25) — enough to
register on the probe but not enough to move the ramp's second segment by anything the eye can catch.
Snow's identical patch produces a real ΔE at the same dilution because its own first segment is *not*
flat (it climbs `BareGroundAlbedo → SettledSnowAlbedo` across exactly that span), so any snow depth
above zero already carries most of snow's albedo lift before dilution enters into it at all — an
asymmetry between the two arms' ramp shapes, not a bug in either.

### Vacuum (§18)

`A = 1`. No atmosphere, no cloud base, no second wall, no cavity. Shaped to `Source/Vacuum.cs`'s
convention exactly: `bool inVacuum` is the **last** parameter on `Amplification` and `CavityGain`, it
is **required rather than defaulted**, and the vacuum value returns before any atmospheric term is
read — so the two albedo arguments are simply *not consulted* in the vacuum arm, and "an orbital
platform's regolith bounces light off nothing" is expressed structurally rather than by a comment.
Every `[TestCase]` pins the vacuum value alongside its sea-level counterpart in one sweep, per the
same convention, so a regression in either shows up as a diverging pair.

`SurfaceBuildup.CavityGainFor` calls `Vacuum.InVacuumForMap` exactly once and passes the bool down
rather than early-returning on it — a second place that knows what vacuum means is a second place that
can disagree with the pure core about it.

### Settings and the feature gate

`CelestialLightingFeatures.SnowAlbedo` (default on; off returns exactly 1 from `CavityGainFor`, so
"off" is a bit-identical pre-§21 baseline). **Not surfaced in the settings screen**, for §18c's
reason: the gain is *derived* from published albedos and §13's own cloud opacity rather than tuned,
so there is no number here a player would sensibly dial. It exists as a harness A/B axis
(`snow_albedo`, bridged in `ProbeRegistration`) and as an escape hatch.

### Outstanding: what the live A/B has to answer

- **Ice sheet and sea ice.** Both are permanently snow-covered, so both become **permanently**
  brighter. That is physically right and probably desirable, but it is a persistent change to some of
  the game's darkest and most hostile biomes, and it wants a deliberate look rather than being
  discovered in play.
- **The overcast night upper bound.** See the extinction note above — a snowy overcast night is
  currently amplified without the deck attenuating the moonlight it is amplifying.
- **Whether full recovery is too much.** A snowy overcast now renders with §13's dimming entirely
  cancelled, i.e. identical to a clear day. That is what the model says and the clamp is the
  renderer's, but "identical to clear" is a large visual step from "18% darker" and it is the kind of
  thing that reads differently in motion than in a table.
- **Whether snow-glare bloom is worth a ticket.** The daytime colour channel has no headroom above
  vanilla's palette, so the physical claim (snowy overcast brighter than snowy *clear* sky) cannot be
  drawn there at all. An additive pass is the only way to express it; whether that is worth the
  complexity is a live-A/B question, not a desk one.
- **The sand arm's own ΔE — measured.** `sand_albedo_cavity.json`, re-run with `SetSand`/`SetTerrain`
  tiled four ways to cover the fixture's full 250×250 map (`MeanSandDepth` = 1.0, the ramp's fully
  saturated case) rather than one 128×128 patch: **overcast night median ΔE 1.36** (visible on close
  inspection), **overcast noon median ΔE 4.48** (visible at a glance, `surface_cavity_gain` 1.1415),
  **clear night ΔE 0.00** (imperceptible — clear weather closes §13's cloud opacity to near zero, so
  there is nothing for the cavity to amplify regardless of buildup depth; `surface_cavity_gain`
  1.0145 confirms the gain itself is real but has almost no opacity to act on). On the reference scale
  (§20c 0.36 … §21 snow 6.06) the sand arm's ceiling sits between §20 site altitude (1.88) and §21
  snow, genuinely visible rather than merely wired — but only once buildup covers most of the map, per
  the dilution note above. A realistic partial dune field will read weaker in direct proportion to its
  coverage fraction of `Map.Area`, which is the same areal-mean honesty the mean-depth model gives
  every other reading of this subsystem, not a special case for sand.
- **The sand arm under Clear weather, with §22's partial cloud cover — measured, item deferred by
  PR #117.** `sand_cloud_cover.json` reuses `sand_albedo_cavity.json`'s own two 128×128 `SetSand`
  patches (roughly half the fixture's map, `MeanSandDepth` ≈ 0.51 — genuinely partial, not the
  full-map saturation the reading above uses) and holds latitude 55, day 5, hour 1 (night — see
  `WeatherDimming.DimmingFor`'s Clear clamp above for why the *sky tint* cannot show anything at noon
  here; since issue #134 a partly-cloudy Clear noon does reach §24's additive lane, but that is glare
  rather than this reading, and over half-map sand the residual is a fraction of the snow case's).
  Before issue #100/PR #120, §21's night-floor cavity read a fixed zero cloud opacity on Clear
  regardless of §22's actual per-map fraction; after it, the cavity reads §22's continuous fraction
  during Clear the same way it already read discrete Overcast/Rain/Fog opacity. With `cloud_cover`
  off, `surface_cavity_gain` is 1.0051 (vs the 1.0000 no-op floor with the effect off) — the discrete
  Clear-sky backscatter alone, reproducing #117's own reading. With `cloud_cover` on, gain is 1.0178 —
  more than triple the cloud-off lift, because now the cavity also has §22's cloud deck to bounce
  light off. Both pairs measured **ΔE 0.00** (cloud off) and **ΔE 0.59** (cloud on) — both imperceptible
  per this doc's ΔE-1 bar, consistent with the reading above: a partial, half-map patch under Clear
  moves gain by roughly 0.5–1.8%, well short of the full-map-saturation overcast readings that do clear
  the bar. So: #100/#120 is confirmed live to change what the cavity reads on Clear (gain roughly
  triples), but that change is not yet visible on screen at this patch size and time of day — the same
  "correct but not yet shipped-visible" outcome §20c records, not a bug in either fix.
  **Harness note, worth keeping for later scenario authors:** `cloud_cover_fraction` was not
  reproducible across fresh runs at first — `CloudCoverDrift`'s noise field has a ~205-year period, and
  the harness's `SetSeason`/`SetTime` jump to a day-of-year *within whatever absolute year the game's
  clock is already in at boot* (documented on `RimWorldTestHarness`'s `ClockProbes.cs`), so "day 5"
  samples a different point in that noise depending on which year the boot happened to land in — three
  fresh runs measured `cloud_cover_fraction` anywhere from 0.14 to 0.22 with no scenario change. Rather
  than pin a loose range, `CloudCoverFractionOverride.cs` (dev-only, same shape and boundary as
  `PlanetsmithTiltOverride`) postfixes `CloudCoverClock.FractionForMap` to force a fixed 0.35 behind a
  `cloud_cover_forced_fraction` harness flag, so every probe above is an exact, reproducible pin
  regardless of which year the fixture boots into.
- Every number above other than the sand arm's is still offline; nothing else in this subsystem has
  been seen in-game yet.


## 22. Partial cloud cover during Clear weather (`CloudCoverDrift` / `SeasonalWetFraction` / `CloudCoverSky`)

**Problem.** Vanilla's "Clear" is a single fixed sky palette for its entire duration —
`WeatherDecider`'s discrete weighted-random state machine has no notion of a fractional cloud amount
anywhere in it. A colony can sit under a flat, cloudless sky for days between weather rolls, which
reads as static in a way real Clear days do not.

**Two separate questions, two separate files.** "How cloudy should a *typical* Clear day read, on
this tile, this time of year" is `SeasonalWetFraction`'s question — reused from vanilla's own
(private) `WeatherDecider.CurrentWeatherCommonality`: each `WeatherDef` on the biome's
`baseWeatherCommonalities` contributes `commonality x commonalityRainfallFactor.Evaluate(rainfall)`,
gated to zero if its `temperatureRange` excludes the tile's seasonal temperature, and the wet-vs-dry
split (`rainRate`/`snowRate` > vanilla's own 0.1 threshold) turns that weighted list into a single
wet-mass-over-total-mass ratio in [0, 1]. That value barely moves within a day, so it is read from
live state (`CloudCoverClock.SeasonalWetFractionFor`) only once per in-game hour. "How cloudy is it
*this hour*, given that average" is `CloudCoverDrift`'s question — the seasonal mean wobbled by
coherent lattice noise, additively rather than multiplicatively (see that file's own header: cloud
cover is a probability, not a loading, so a bone-dry tile must still be *able* to see a passing cloud
rather than being structurally pinned to zero).

**The noise shape is deliberately faster than §20c's aerosol drift.** Aerosol's 3-day lattice cell
exists so nothing visibly changes within one evening — flicker there would fight §8's smooth
elevation ramp. Cloud cover's brief is the opposite: real skies change over a single afternoon, and
are supposed to. An 8-hour base cell (`CloudCoverDrift.LatticeCellHours`) with three noise octaves —
against aerosol's two — puts the fastest layer at 2 hours, comfortably inside a single day, so
octaves occasionally align to produce a faster net swing than any one layer alone. That is the
"usually drifts, but can shift within an afternoon" character, falling out of the noise shape rather
than a special case.

**The colour contribution lerps toward vanilla's own overcast anchors, not a bespoke palette.**
`CloudCoverSky.SkyTintFactor`/`SaturationTintFactor` interpolate from 1 (no change) at cloud cover 0
to `WeatherDimmingMath`'s existing Overcast/Clear luminance and saturation ratios (0.8 and 0.72) at
cloud cover 1 — the same destination §13's `WeatherDimming` already renders Overcast/Rain/Fog toward.
A fully-clouded Clear day therefore reads as "partway to Overcast" using art vanilla already ships,
not as a CelestialLighting-specific tint layered on top of it. `Patch_CloudCoverSky` gates strictly on
`curWeather == Clear` (not `lastWeather`, no transition blend — see that file's header for why
inventing an axis vanilla's state machine has no notion of is not worth cross-fading against a
transition into a different weather entirely) and multiplies `.colors.sky`/`.colors.overlay`/
`.colors.saturation` rather than assigning, so it composes with whatever earlier postfix already ran
(§2's twilight warmth, §8's colour temperature, §11's aurora tint). `.glow` is untouched — same
gameplay-scope discipline as §13's `WeatherDimming`.

**Never fights `Patch_WeatherDimming` for the same pixels, with no ordering declared.** Both patches
target `WeatherWorker.CurSkyTarget`, but their gates are mutually exclusive by construction:
`WeatherDimmingMath` classifies Clear as opacity 0 on both its axes, so `Patch_WeatherDimming`'s own
early return always fires while the weather is Clear, and `Patch_CloudCoverSky`'s early return always
fires whenever it is not. Neither patch's output depends on whether the other ran.

**The UI half.** `Patch_CloudCoverLabel` appends "- N% cloudy" to `WeatherManager.DoWeatherGUI`'s
label whenever `CurWeatherPerceived` reads Clear (e.g. "Clear - 50% cloudy"), a full-body Prefix
replacement rather than a transpiler — see that file's header for why this codebase prefers
duplicating a short, stable vanilla method over an IL-shape patch across a RimWorld update. Gated on
`CurWeatherPerceived`, not `curWeather`: the label already tracks whichever weather WeatherManager
judges visually dominant mid-transition, so the suffix asks the same question the label text itself
is built from rather than the state-machine field `Patch_CloudCoverSky` gates on. Shown at every
reading including 0% — a player watching the label to confirm the feature is alive should see a
stable readout every time it's Clear, not have it silently vanish on a calm hour. Because of that,
this patch checks `CelestialLightingFeatures.CloudCover` directly rather than leaning on
`FractionForMap`'s own zero-return the way `Patch_CloudCoverSky` does: a calm-but-on 0% and an
off 0% now read as visibly different strings ("Clear - 0% cloudy" vs plain "Clear"), so only an
explicit flag check keeps "off" reproducing the pre-feature label exactly.

### Settings and the feature gate

`CelestialLightingFeatures.CloudCover` (key `cloud_cover`). Off returns exactly 0 from
`CloudCoverClock.FractionForMap`, which is what makes "off" a faithful pre-feature baseline for both
`Patch_CloudCoverSky` and `Patch_CloudCoverLabel` at once — see the flag's own note on why "off" must
mean this, not merely "usually small". `WobbleAmplitude` (0.35, `CloudCoverDrift`) is a starting
guess, picked the same eyeballed way as §20c's `AerosolDrift.DriftAmplitude`, not because the two
quantities are physically comparable.

### Live verification

`Tests/Scenarios/cloud_cover.json`, latitude 45, day 40, Clear weather held instant. The off/on
invariant is exact: with the feature off, `cloud_cover_fraction` reads `0.0000` regardless of time of
day. With it on, an hourly survey from noon to 8pm reads (all pinned live, ±0.0005): `0.2113, 0.2152,
0.2170, 0.2432, 0.2710, 0.2277, 0.1376, 0.0792, 0.0435` — the seasonal-mean-plus-noise drift §22's
design predicts, peaking mid-afternoon on this tile/season and never approaching either bound.

The A/B screenshot pair sits at the surveyed peak (hour 16, cloud cover 0.2710):

median CIELAB ΔE **2.74** — visible at a glance. Against the measured set so far (§20c aerosol drift
0.36, §19b ozone column 1.48, §20 site altitude 1.88, §21 snow cavity at overcast noon 6.06, §20b
pollution at 1.0 6.79), §22 lands solidly mid-pack: a real, noticeable shift at roughly a quarter
cloud cover, well short of a full-overcast repaint.

- **Not yet surveyed at other latitudes/seasons.** The pinned ladder is one tile's one week; a wetter
  or drier biome, or a season where `SeasonalWetFractionFor`'s temperature gate excludes more of the
  weather list, has not been looked at.
- **The label suffix has not been screenshotted.** The sky-colour A/B above confirms the render half;
  the UI half's correctness rests on the offline `Patch_CloudCoverLabel` reasoning alone.


## 23. Cloud-base underlighting (`CloudUnderlightMath`, issue #88 option 1)

**Problem.** A genuinely clear-sky sunset is monotonous — one smooth gradient, the case §8 already
models. Essentially all of the drama people associate with real sunsets comes from cloud *bases* lit
from beneath: once the sun sits at or below the horizon, direct light no longer reaches the ground,
but it still reaches a cloud deck's underside for a while longer, having crossed an extremely long
low-elevation path and arriving heavily reddened. Cloud altitude sets both the timing and the
character of that light — issue #88's own table: high cirrus (~10 km) stays lit well below the
horizon and reads as lingering pinks/magentas, mid altocumulus (~4 km) catches only the last of it as
deep orange, and low stratus (~1 km) goes dark almost immediately, which is real weather's own way of
"ruining" a sunset rather than a bug in a warm-tint model that only ever adds warmth.

**Option 1, not option 2 — and why.** Issue #88 lays out both honestly: a real cloud-underlit sky is a
*spatial* effect (warm cloud against a cool vault), which RimWorld's single flat sky colour cannot
represent without an §11a-aurora-sized new render path (`SectionLayer`, its own performance budget,
the whole shape of #60/§20's cost history). Option 1 instead modulates the *strength* of §8's existing
single-colour tint by how much of the cloud deck is still catching direct light — no new colour
target, no new geometry on screen, just getting the timing and intensity of a mechanism §8 already
has right. The issue's own recommendation is to ship option 1 first and only revisit option 2 if a
live A/B shows the flat version reading as mud; that A/B is the "Live verification" subsection below.

**Option 2 now exists too, as §23b.** It was taken up not because the A/B below read as mud but
because it read as *weak* — the suppression half measured 1.32 at its deepest surveyed point — and
because §24 had meanwhile built the additive pass that made option 2 affordable. The two are
complements rather than replacements: §23 owns the sky's mean colour and the deck weathers, §23b owns
the structure and (in practice, on a vanilla install) the Clear ones. See §23b for the partition.

**The geometry reuses §19's Earth-shadow model, inverted.** `PurpleLightMath.ShadowHeightKm` already
answers "how high has Earth's own shadow climbed, given the sun is this far below the horizon" —
`h(theta) = R * (sec(theta) - 1)`. `CloudUnderlightMath.ShadowEntryDepressionDegrees` asks the inverse
question: given a cloud base at height `h`, at what depression angle `theta` does the rising shadow
finally reach it — `theta(h) = arccos(R / (R + h))`. Deliberately the same secant-shadow model rather
than the coarser small-angle horizon-dip approximation aviation/marine navigation normally uses: this
codebase already has one canonical answer for "how high is Earth's shadow" (§19), and a second,
slightly different approximation of the same physical quantity is exactly the kind of drift the
mired-space and aerosol notes (§20, §20d) warn against.

**Two phases, meeting at the deck's own shadow-entry angle.** `GlowPhase` is `4t(1-t)` across the
window from the horizon (t=0) to shadow entry (t=1) — zero at both ends, peaking at the window's
midpoint, with the zero-at-both-ends shape removing any seam at either boundary once the window's
width is fixed by the geometry above. `ShadowSuppressionPhase` picks up exactly where `GlowPhase`'s
window ends and ramps to full (1) at §8's own `NightFadeFloorDegrees` — the point §8's tint is already
zero, so "full suppression" there multiplies an already-zero contribution rather than doing anything
of its own. `WarmthMultiplier` combines them as `1 + opacity * (0.6 * glow - suppression)`: a high deck
(large shadow-entry angle) spends most of the below-horizon range in its glow phase and reads above 1;
a low deck (shadow-entry angle near 0) has almost no glow phase and spends nearly the whole range in
suppression, reading below 1 and down to a floor of exactly 0. Same input, opposite sign, by
construction rather than as a tuned special case — this is issue #88's headline invariant.

**Altitude is a second axis on the same escape hatch §13 already has.** `WeatherCloudDeck.opacity`
already lets a `WeatherDef` state its cloud coverage outright rather than being classified from
palette/precipitation. `altitudeMetres` (default unset, sentinel -1) is the same shape one field over:
declared and used only once `OverridesAltitude` is true, everything else keeps being classified
automatically. The automatic classifier
(`WeatherDimmingMath.DefaultAltitudeMetres`) blends between `DryDeckDefaultAltitudeMetres` (4000 m)
and `PrecipitatingDeckDefaultAltitudeMetres` (1000 m) by the same `ObscurationIntensity` §13 already
computes from rain/snow/sand rate — chosen to land on issue #88's own worked values for the dry and
precipitating rows of its table (mid altocumulus ~4 km, low stratus ~1 km). The classifier
structurally cannot reach the table's third row (high cirrus ~10 km): nothing about a rain rate says
whether it is falling from a low nimbostratus or an unusually tall storm cell, so that row is reachable
only through an explicit `WeatherCloudDeck.altitudeMetres` override, same as §13's opacity residue.

**Modulates §8's tint, never introduces a second colour target.** `Patch_SkyColorTemperature` computes
`tint` exactly as it did before §23, then — gated on `CelestialLightingFeatures.CloudUnderlight`
directly, not merely on `CloudAltitudeMetresFor`'s own internal zero return, because 0 is a legitimate
real altitude for a ground-hugging deck and not a sentinel for "feature off" — multiplies it by
`CloudUnderlightMath.WarmthMultiplier` before blending toward the same target colour §8 already
computes. `.colors.saturation` and `.glow` are untouched, the same colour-only discipline every
subsystem in this lane keeps.

### Settings and the feature gate

`CelestialLightingFeatures.CloudUnderlight` (key `cloud_underlight`), default on. Off skips the
multiplier entirely inside `Patch_SkyColorTemperature`, which is what makes "off" reproduce §8's
pre-§23 tint bit-for-bit — the harness A/B baseline. Coupled to `WeatherDimming` the same way §21's
`SnowAlbedo` is: with `WeatherDimming` off, `CloudOpacityFor` already reads 0 everywhere, so §23
silently has nothing to modulate regardless of its own flag — an honest consequence of building on
§13's opacity axis, not a bug. `GlowAmplitude` (0.6, `CloudUnderlightMath`) is a starting guess picked
the same eyeballed way as §22's `WobbleAmplitude`, kept modest relative to the suppression side's full
[0,1] range so a lit deck reads as more saturated colour rather than an unboundedly stronger blend.

### Live verification

`Tests/Scenarios/cloud_underlight.json`, latitude 45, day 40. Civil dusk on this tile/season falls
between hour 20.5 and 21 (found by survey, per the parent CLAUDE.md's "survey before you pick an
hour" note); two real installed `WeatherDef`s stand in for issue #88's table rows with no custom Def
needed — Overcast classifies dry (`WeatherDimmingMath`'s automatic classifier: 4000 m, "mid
altocumulus") and Sandstorm classifies fully precipitating (1000 m, "low stratus").

The off/on true-no-op invariant is exact everywhere in the sweep: with the feature off,
`cloud_underlight` reads `1.0000` regardless of weather or time. All multiplier values below are
pinned live (±0.0005).

At hour 20.80 (sun elevation **-1.4900°**, pinned), the two decks land on opposite sides of 1.0 for
the same input, issue #88's headline invariant: Overcast (4000 m) reads **1.4685** — still inside its
glow window (shadow-entry angle ~2.03°) — while Sandstorm (1000 m) reads **0.9047** — already past its
own shadow entry (~1.02°) and into suppression. The two phases are not symmetric: `GlowPhase`'s
`4t(1-t)` hump is already well past its own peak by -1.49° for a 4000 m deck, while Sandstorm's
suppression window is barely 10% travelled at the same elevation, having only just entered it. That
asymmetry shows up directly in the measured deltaE:

| capture | condition | median CIELAB ΔE |
|---|---|---|
| `cu_overcast_off.png` / `cu_overcast_on.png` | Overcast, -1.49°, glow near its peak | **2.10** — visible at a glance |
| `cu_sandstorm_off.png` / `cu_sandstorm_on.png` | Sandstorm, -1.49°, suppression just started | **0.57** — imperceptible |
| `cu_sandstorm_deep_off.png` / `cu_sandstorm_deep_on.png` | Sandstorm, -3.86°, suppression well advanced | **1.32** — visible on close inspection |

**The crossover pair is the correctness demonstration, not the strongest visual one, and that is
worth stating plainly rather than picking a flattering hour and leaving it there.** The exact
elevation where the two decks' signs first cross is also the elevation where Sandstorm's own
suppression has barely started, so the "opposite sign" pair understates the low-deck case on its own.
Surveying deeper (-3.86°, `cu_sandstorm_deep_*.png`) shows the suppression clearing 1.0 — closer to,
but still short of, "at a glance". Against the measured set so far (§20c aerosol drift 0.36, §19b
ozone column 1.48, §20 site altitude 1.88, §21 snow cavity at overcast noon 6.06, §22 cloud cover
2.74, §20b pollution at 1.0 6.79), §23's boost half lands mid-pack and its suppression half is the
weakest live-verified subsystem in the mod short of §20c — an honest consequence of option 1's own
scoping, not a bug: modulating a single flat colour by a fraction that only reaches 1-in-10 of its
own suppression window at the crossover elevation was never going to read as strongly as a full
colour-target change would.

- **The suppression case never reaches "visible at a glance" on this tile/season.** Even at the
  deepest surveyed point (-3.86°, mult 0.4297 — more than half suppressed) the measured ΔE is 1.32.
  Part of this is structural: `WarmthMultiplier`'s suppression term is riding on top of §8's own
  `TintStrength`, which is itself fading toward zero as elevation approaches
  `NightFadeFloorDegrees` — multiplying a shrinking base tint by a shrinking multiplier compounds
  into a smaller absolute colour change than the arithmetic on the multiplier alone suggests. Whether
  a still-higher-altitude override (issue #88's ~10 km cirrus row, only reachable via
  `WeatherCloudDeck.altitudeMetres`) reads more strongly has not been surveyed.
- **Not yet surveyed at other latitudes/seasons**, and not yet surveyed with an explicit
  `WeatherCloudDeck.altitudeMetres` override standing in for the table's cirrus row — only the two
  automatic classifier defaults have been measured.


## 23b. The underlit cloud LAYER (`CloudField` / `CloudUnderlightOverlay`, issue #88 option 2)

**Status: SHIPPED OFF** (`cloud_underlight_layer`), the same prototype posture §24 took, and for the
same reason: issue #88 and epic #103 both record an open question that cannot be settled by argument,
only by looking. RimWorld is top-down with fixed exposure, so warm patches drifting across the ground
may read as sky drama or as stains on the terrain. The frames below are committed so the call can be
made by looking.

**Problem.** §23 above is explicit about what it does not do. It modulates the *strength* of §8's one
flat sky colour, which gets the timing and intensity of cloud-base underlighting right — and issue
#88's actual mechanism is *spatial*: warm underlit cloud standing against a cool vault is a
difference between two places on screen. One colour cannot be two colours, and averaging them gives a
neutral mud that is worse than not trying. §23's own live verification is what promoted this from
"maybe later" to "now": its suppression half measured **1.32** at its deepest surveyed point, the
weakest live-verified result in the mod short of §20c, precisely because a single flat colour scaled
by a fraction has so little room to move.

**SCOPE, AND IT IS THE MOST LOAD-BEARING PARAGRAPH IN THIS SECTION. §23b draws the light a cloud deck
BOUNCES BACK DOWN ONTO THE GROUND. It is illumination, not a picture of clouds.** That distinction was
not in the original design; it came out of watching the first build, where the reasonable reaction was
"this shades patches of the map and barely looks like clouds". Both halves of that are right, and only
the second is a complaint about the wrong thing. Bounced light off a deck is *diffuse by
construction* — every point on the ground sees a large solid angle of lit cloud — so it varies
smoothly over tens of cells and has no edges of its own to draw. A sharper field would not read as
more cloud-like; it would read as cloud SHADOWS painted onto the terrain, which is a different and
wrong claim about where the light is coming from. Clouds that actually look like clouds on screen are
issue #138, a separate subsystem alongside this one rather than a refinement of it — and if it lands,
the two must share one field, because bright ground under a drawn gap is the whole point.

**The partition: the flat lane carries the MEAN, this lane carries what is above it.** The obvious
implementation — draw warm light proportional to how underlit the deck is — would render a second
time what §23 already renders through §8's tint. `CloudField.Residual` instead subtracts the
field's own areal mean, so what is drawn at a point is how much *more* underlit cloud sits there than
the map average. This is the same "two lanes, one quantity" shape `SnowGlareMath.UndrawableExcess`
uses against §21 one subsystem over, and both degenerate skies fall out of it rather than being
special-cased:

| sky | what this lane draws | why |
|---|---|---|
| cloud fraction 0 | exactly nothing | no cloud, no structure — and §23 has nothing to modulate either |
| cloud fraction 0.5 | the most it ever draws | half lit deck, half open vault: the largest contrast a sky has |
| cloud fraction 1 | exactly nothing | an unbroken deck is uniform, i.e. exactly the sky one flat colour describes perfectly — all of it is §23's |

**It covers the sky §23 structurally cannot, which is the more common one.** §13 scores Clear as cloud
opacity 0 on both axes, so §23 is silent on a clear evening by construction — and the *audit* makes
that sharper than it looks: across 81 installed `WeatherDef`s, §13's classifier scores every vanilla
weather as either 0 or 1 and nothing in between (the seven partial values are all workshop weathers,
§13's own documented judgement-call residue). So on a vanilla install the only route to a partly
covered sky is §22's Clear-weather cloud fraction, and §23b is in practice a **Clear-weather
subsystem** whose complement is §23's deck-weather one. `CloudUnderlightLayer.CloudFractionFor` reads
both and takes the larger, which is a max over a pair that always contains a zero rather than a blend
of two opinions — the same mutual exclusivity `Patch_CloudCoverSky` already relies on.

**The strength reuses §23's own window rather than deriving a second one.**
`CloudUnderlightMath.LayerStrength` is `amplitude x GlowPhase(elevation, shadowEntry)` — the same
`GlowPhase`, off the same inverted Earth-shadow geometry. Keeping one canonical answer to "how high
is Earth's shadow" is why §23's geometry was built on §19's model in the first place (see §23 above,
and §20/§20d on mired-space drift); a private copy here would reintroduce that drift one subsystem
later and let the two lanes disagree about when a sunset ends. It carries **no opacity term**: how
much cloud there is enters through the field, and multiplying by it again would make a full overcast
the strongest spatial case when it is the one case with nothing spatial to say. The suppression phase
is likewise absent — killing a sunset is something done to a flat colour, not something an additive
pass can draw, so issue #88's "a low overcast ruins it" case stays entirely §23's.

**The colour is a GRADIENT between two ends, and that is where the drama actually is.** The first
build tinted the whole field with one colour — `SkyColorForElevation` at the current elevation, §8's
own target — on the reasoning that §23b's novelty was spatial and the codebase should keep one
answer per physical quantity. Watching it showed the flaw: a single warm tint spread over the field
adds warm light *everywhere*, which reads as the map being turned up, and is a thing §23's flat lane
already does one lane down. What makes a real sunset dramatic is that the light reaching the ground
is a **different colour in different directions** — deck toward the sun is lit through the longest,
reddest path and bounces deep orange down; deck away from it is lit by the anti-solar twilight sky
and bounces pink.

So the bake blends between two ends, and both are borrowed rather than invented: the sunward end is
§8's `SkyColorForElevation` as before, and the anti-solar end is §19c's `PurpleLight.ComposedHueFor`,
this codebase's existing answer for twilight purple (delegated to §19c's adapter, not recomputed, so
the two cannot disagree about what that is). One colour authority became two *existing* colour
authorities, which is a different thing from inventing a palette.

**The gradient's axis is quantised to the eight directions that tile, and that is a real compromise.**
The texture repeats and pans — that is what makes drift free — so anything baked into it must be
periodic over the tile, or a colour seam sweeps across the colony once per drift cycle. A gradient
along an arbitrary sun azimuth is not periodic; a cosine along an *integer lattice* direction is. So
`GradientAxis` rounds the sun's bearing to the nearest of the eight (at cos 67.5°, so each owns an
equal 45° arc), and the colour lobes lie along that. The axis therefore tracks the sun to within
22.5°, and the lobe's *phase* rides with the field rather than being pinned to the sun. Acceptable
because a top-down player cannot see the sun: what reads is that the light differs across the map.
`TheColourGradientWrapsSeamlessly` pins the periodicity for all four axis families.

**A texture, not vertex colours, and that is a deliberate refusal to guess.** §15b's eave shade proves
per-vertex colour is honoured through `ShaderDatabase.Transparent` — but this pass must be ADDITIVE
(`MoteGlow`), and nothing in this codebase has ever asked `MoteGlow` to honour a vertex colour.
`SheetMaterial`'s header records why that is not a guess worth making: the shader is not ours, and
being wrong renders something plausible rather than nothing. §11a needed exactly this and used a
texture, so §23b follows the path already known to work here — and gets bilinear soft edges (epic
#103's first-class requirement, free rather than as inset rim geometry) and drift-as-a-UV-pan out of
it. The tile is seamless by construction because `AuroraNoise` wraps on an integer lattice.

**The bake is split where the cadences differ.** The field's *shape* depends only on the cloud
fraction, which moves a few times an in-game hour; its *colour* is §8's target, which moves every
frame as the sun sets. So the noise walk runs on the fraction and the byte write runs on the colour.
Without that split, a subsystem whose entire life is the ten minutes around sunset would re-walk
4,096 noise samples per frame during exactly those ten minutes.

**The threshold is read off each tile's own histogram, and the fixed version was wrong in a way that
would have shipped.** A fixed cut through fractal value noise is a cut through the steep part of a
peaked histogram, and one texture tile is a small sample (`LatticeCells` is 4, so 16 base cells).
Measured across three tile seeds, one fixed cut produced covered areas of **0.41, 0.78 and 0.70** for
the same requested 0.5 — "how cloudy is it" would have meant something different on every colony.
`ThresholdFor` takes the quantile instead, which makes covered area exact on any seed and deleted the
two tuning constants that were standing in for it.

### Settings and the feature gate

`CelestialLightingFeatures.CloudUnderlightLayer` (key `cloud_underlight_layer`), default **off**. Off
is a true no-op: `StrengthFor` returns 0 and the overlay returns before its draw call, so rendering is
bit-identical to pre-§23b and the standing cost is genuinely zero. Independent of §23's own flag in
both directions — `WeatherDimming.CloudAltitudeMetresFor` was widened to survive either, because
gating it on the flat lane alone made "flat off, spatial on" return a ground-hugging 0 and silently
kill the layer for a reason no setting names.

`LayerAmplitude` is **0.10**, and it got there by watching rather than by arithmetic. The first
calibration was 0.20, which measured median ΔE 9.12 — larger than anything the mod ships — and read
as distracting over a sunset rather than as part of one. The old value stays reachable in one boot
through `CloudUnderlightLayer.AmplitudeScale` (`cloud_underlight_strong`), which is what stops the
decision from becoming a claim about a build nobody can rebuild: the two frames differ in exactly one
constant, from one process, at one instant.

### Live verification

`Tests/Scenarios/cloud_underlight_layer.json`, latitude 45, day 40 — the tile and season §23's own
scenario established civil dusk on — with every hour located by
`cloud_underlight_layer_survey.json` rather than reasoned about. **That survey was not a formality:**
§23b's glow window runs from the horizon only down to the deck's shadow entry (~2.03° for a 4000 m
deck), about **seven minutes** on this tile, so an hourly grid does not under-sample it, it misses it
entirely. Peak strength is at hour 20.78 (`sun_elevation` **-1.1715°**, pinned); the window is shut by
20.84 (**-2.1251°**).

| capture | condition | `cloud_underlight_layer` | median CIELAB ΔE vs off |
|---|---|---|---|
| `cul_clear_on.png` | Clear, §22 cover 0.35, **shipped 0.10** | 0.0976 | **4.31** — visible at a glance |
| `cul_clear_strong.png` | the same, swept back to the first guess | 0.1952 | **8.41** — obvious, and too much |
| `cul_overcast_on.png` | Overcast, cover 1.00 | 0.0976 | **0.00** — 0.0% of pixels changed |
| (past shadow entry, -2.13°) | Clear, below the window | 0.0000 | **0.00** — 0.0% of pixels changed |

**The colour gradient measured, because a whole-frame mean cannot see it** — it averages exactly the
contrast being claimed. Splitting one frame into a 4x4 grid and measuring the light §23b *added* per
cell (on minus off, per channel) at the shipped amplitude:

```
+13.2/ 8.2/ 3.5    + 8.0/ 4.0/ 0.2    + 2.3/ 1.2/ 0.0    + 5.7/ 3.5/ 1.5
+ 3.5/ 2.2/ 0.9    + 8.2/ 4.0/-0.2    +14.5/ 7.3/ 0.4    +16.8/10.7/ 5.3
+ 2.1/ 1.3/ 0.5    + 8.6/ 4.3/-0.2    +16.3/ 8.2/ 0.4    +16.3/10.4/ 5.1
+ 0.1/ 0.0/-0.0    + 1.3/ 0.6/-0.1    +13.2/ 6.7/ 0.3    + 2.9/ 1.7/ 0.5
```

Red-minus-blue runs from 0.1 to 15.9 across one frame. The pink end reaches `+16.8/10.7/5.3` and the
orange end `+16.3/8.2/0.4` at nearly the same brightness — same amount of light, visibly different
colour, in one frame. That is the claim, and it is the thing the flat lane cannot make at all.

### Over time

`Tests/Scenarios/cloud_underlight_layer_lapse.json` films three sweeps: the whole evening with the
layer on (`cul_evening_on.mp4`), and the glow window itself five times slower, on and off
(`cul_window_on.mp4` / `cul_window_off.mp4`). Measuring the two window sweeps against each other frame
for frame — same hour in both — gives the effect's envelope rather than one instant of it:

| hour | 20.60–20.66 | 20.68 | 20.72 | **20.776** | 20.82 | 20.84+ |
|---|---|---|---|---|---|---|
| median ΔE | 0.00 | 1.05 | 2.64 | **4.32** | 2.20 | 0.00 |

It blooms in and out over **~10.5 game minutes**, centred about 1.2° below the horizon, with no seam
at either end — `GlowPhase`'s `4t(1-t)` shape doing exactly what it was chosen for. As a sunset event
that is the right behaviour: real cloud-base afterglow is a brief flare, not a state.

**The drift does not read within one sunset, and that is by construction rather than by oversight.**
The field pans one full tile per 7,200 ticks, so a 10-minute window advances it about 6% of a tile.
Over an evening it is a slow slide; inside the event it is effectively frozen. Making patches form
and dissolve *during* a sunset would need time as a third noise axis, and is deliberately not done —
real cloud fields do not reshape in ten minutes either.

**`Tools/CloudPreview` renders the field offline**, to PNGs and with a bake timing, because "does this
look right" is a question about the field's shape and answering it through the harness costs a full
RimWorld boot per iteration. It is also the wrong instrument for it: in game the field is composited
at low alpha over dark terrain, so a shape problem and a strength problem are hard to tell apart. Same
premise as `Tools/AuroraPreview` next door, and it links the shipped file rather than reimplementing
it.

**The Clear pair is the headline and the Overcast pair is the point.** At the same instant, §23's own
multiplier reads exactly **1.0000** under Clear (it is doing nothing at all — §13 scores Clear as
opacity 0) and **1.5857** under Overcast, while §23b does the reverse. The two lanes cover disjoint
skies, and the Overcast null is the mean subtraction working: a solid deck has no gaps, so there is no
structure to draw however strong the layer is. Any visible difference in that pair would mean the two
lanes had started double-counting, which is exactly what §24's night pair is evidence against for
§21.

**4.31 sits mid-pack, and the first guess did not.** At 0.20 the layer measured 9.12, larger than
anything the mod ships (§20b pollution 6.79, §21 snow cavity 6.06), and watching a sunset at it was
what settled the question the number could not: it was distracting rather than atmospheric. Halved.
Against the measured set, §23b's shipped 4.31 now sits between §22's cloud cover (2.74) and §21's
snow cavity (6.06) — noticeable, not the loudest thing on screen.

- **The map is dark at this hour and that is not a scene-lighting mistake, it is where the effect
  lives.** Mean frame colour with the feature off is `rgb(27, 17, 10)`. An earlier run measured
  `rgb(2, 1, 1)` — literally black — for two harness reasons worth recording: `ModSettings` XML sits
  outside the harness's claim ledger, so Realistic (left persisted by an earlier `snow_glare` run)
  zeroed the brightness floors, and the fixture's map was still fogged, which draws neither terrain
  nor things. The scenario now pins `realistic_preset` false and paints terrain map-wide to unfog it.
- **Only one tile, one season and one cloud fraction have been measured**, and the fraction is forced
  (`cloud_cover_forced_fraction`, 0.35) rather than live. Nothing has been surveyed at a fraction near
  either end, where the field's structure is by design weakest.
- **The gradient's phase is not pinned to the sun**, only its axis (see above). Nobody has checked
  what that looks like across a whole day, when the axis steps between its eight directions — a step
  recolours every texel at once, and whether that reads as a change in the weather or as a glitch is
  unsurveyed.
- **The colour has only been measured at one elevation.** Both ends move with the sun (§8's target and
  §19c's hue are both elevation-driven), so the gradient's own contrast is a curve nobody has plotted.

### Performance

`Tests/Scenarios/cloud_underlight_layer_profile.json`, two windows for the same reason §24 uses two —
one of them alone would be a lie. `drawing` is the surveyed peak of the glow window, a few minutes a
day; `gated` is the identical build at noon, which is what essentially every frame of every save is.

```
drawing (Clear + cover 0.35, hour 20.78, 601 frames)
  Patch_CloudLayersDraw:Postfix   avgMsPerFrame 0.2240   maxMsPerFrame 0.7500
                                      callsPerFrame 2.00     avgUsPerCall 112.18
                                      1.344% of a 60 fps budget

gated out (noon, sun above the horizon)
  Patch_CloudLayersDraw:Postfix   avgMsPerFrame 0.0059   maxMsPerFrame 0.0236
                                      callsPerFrame 2.00     avgUsPerCall 2.97
                                      0.036% of a 60 fps budget
```

**38x between the two, and the gate that buys it is the sun's elevation.** `StrengthFor` asks for the
elevation against the widest possible window (§8's own fade floor) before it asks anything about
weather, cloud or the map kind — so a frame outside the window costs one bool, one memoised float and
a compare, and never reaches `WeatherDimming.CloudOpacityFor`, which `MapSky`'s header records as
deliberately un-memoized. `callsPerFrame` 2.00 is `GameConditionManagerDraw` recursing into its parent
manager, with the identity guard early-returning on one of the two, so per this repo's own note about
call counts including early returns the real drawing pass is ~224 µs and the mean understates it.

`maxMsPerFrame` 0.75 against a 0.224 mean is unremarkable — notably *unlike* §24's 3.61, because the
open-sky mask it shares is already built by the time this draws.

### The mask is now shared (epic #103)

`SnowGlareMask`/`SnowGlareMaskMath`/`Patch_SnowGlareRoofInvalidation` became
`OpenSkyMask`/`OpenSkyMaskMath`/`Patch_OpenSkyMaskInvalidation`, and the mesh's UVs changed from 0..1
per quad to map space so a tiling texture can be drawn through it. §24 cannot observe the change (its
material has no texture, so the sampler returns Unity's default white whatever the coordinates say) —
argued rather than unit-tested, because UVs live in the Unity-side adapter, and then checked by
re-running `snow_glare.json`: median ΔE **0.00** against the committed frame, with an identical mean
frame colour of `rgb(144, 131, 121)`.

That is one consumer's worth of epic #103's "one draw, many contributors", arriving as a shared asset
rather than as an API guessed at in advance. What is still NOT shared is the draw call itself: §24 and
§23b each own a `GameConditionManagerDraw` postfix and each issue their own `Graphics.DrawMesh`. They
are independent additive contributions to one frame, so nothing is wrong — but the epic's "one draw"
half is unbuilt, and it will stay that way until #3's sun shafts give it a third consumer.


Two cross-cutting settings ideas that span the subsystems above:

- **Opinionated presets.** Ship a small number of named presets (e.g. "Realistic" vs
  "Cinematic/Pretty") that set the correlated knobs together — shadow length/strength (§1),
  desaturation strength (§9), weather dimming (§13), and the two minimum-brightness floors (outdoor
  §7, indoor §7b) — so most players pick one preset and never open a slider. Individual sliders
  remain for anyone who wants them.

  **§19's "Polar blue strength" is deliberately NOT in the bundle.** It is a per-effect intensity
  like `purpleLightStrength`, not one of the taste axes the six bundled knobs correlate along, and adding a
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

## 24. Snow-glare bloom — the additive pass (`SnowGlareMath` / `SnowGlare` / `SnowGlareOverlay`, issue #90)

**Status: SHIPPED ON**, after the prototype phase answered #90's open question from frames. It shipped
`false` through that phase deliberately, so the taste call could be made by looking rather than by
argument; the measured table below is what settled it.

**What default-on does and does not claim.** It renders the visible-but-restrained half of #90: a
snowed-in overcast noon at median CIELAB ΔE 5.13, a polar snowfield at 3.67-5.08, a partly-cloudy
Clear noon at 1.13 (issue #134), alongside §21's own 6.06. It does **not** render the headline inversion — a snowy overcast brighter than a snowy CLEAR
sky still needs roughly ΔE 15 and reads as milky haze at that strength. Anyone reopening that trade
should start from the sweep below rather than from the physics.

**Problem.** §21 claims a snowy *overcast* is brighter than a snowy *clear* sky — the counterintuitive
inversion the whole subsystem exists to demonstrate. §21 cannot render it. `SkyColorSet.sky` is a
multiply into `MatBases.LightOverlay.color` and vanilla's brightest palette is Clear's `(1,1,1)`,
i.e. "do not darken", so `AlbedoCavityMath.RecoveredDimming` clamps at zero dimming and the ordering
flattens to a tie. Issue #90 records that boundary; this section is the attempt to cross it.

**The shipped quantity is the RESIDUAL, not the gain.** The obvious implementation — draw glare
proportional to `cavityGain` — would double-count what §21 already delivers through the multiply lane
and would fire on maps where that lane has headroom to spare. `SnowGlareMath.UndrawableExcess` instead
returns exactly what `RecoveredDimming`'s clamp discarded: `(1 - dimming) * cavityGain - 1`, floored
at zero. So the two lanes **partition one product** rather than both rendering it —

| condition | multiply lane | additive lane |
|---|---|---|
| bare ground | renders all of it | exactly 0 (gain is 1, nothing overflows) |
| thin deck over snow | renders all of it | small — glare ramps with the deck rather than switching on |
| snowy overcast | renders up to parity | the remainder |
| partly-cloudy **Clear** | renders *none* of it (no dimming to spend) | **all** of it, `gain - 1` |

**A partly-cloudy Clear day fires, and that last row is the whole of issue #134.** §24 originally
inherited §13's view that Clear is *no cloud deck at all*, so `UndrawableExcessFor` returned before the
cavity was consulted and a visibly-clouded snowy Clear day rendered nothing while the same map's
*night* floor was already being amplified by exactly that cloud fraction (#100, #120). The daytime
mirror of that fix is `WeatherDimming.DeckOpacityFor`: §13's classifier off Clear, §22's continuous
cloud-cover fraction on it, through the same `AlbedoCavityMath.EffectiveCloudOpacity` the night arm
uses, so there is one answer to "is there a deck overhead" rather than one per time of day.

**It moves §21's daytime arm by exactly nothing, and the reason is arithmetic rather than a gate.**
The two daytime consumers now read the same deck, which is what #134 was worried about — but only §24
can render it. Dimming stays 0 on Clear (a clear sky genuinely does not darken the ground, and §22
already draws its own sky tint in `Patch_CloudCoverSky`; deriving a second dimming from that same
fraction would darken a partly-cloudy day twice). With `dimming` 0 and a gain above 1,
`RecoveredDimming`'s `1 - (1 - 0) × gain` is negative and clamps, so `DimmingFor` returns exactly 0 on
Clear as it always has — pinned by the `weather_dimming 0.0000` probe sitting next to the glare probes
in both scenarios. The multiply lane is not switched off on Clear; it has no headroom to render into.

**The boundary is a small step rather than a smooth ramp, and that is deliberate.** A sky §22 reports
as exactly cloudless still returns 0, because `DeckOpacityFor` stops the read before the cavity. The
first sliver of cloud brings the clear-sky cavity (≈1.07× over fresh snow) with it, so the residual
jumps 0 → ≈0.073 — measured live at ΔE **0.38**, imperceptible, and about 3.5% of `MaxIntensity` once
scaled. The alternative is to let a genuinely cloudless snowy noon glare on the strength of that same
1.07×, which would fire on every snowy map in the game at midday; that is a much larger scope claim
than #134 asked for and is not made here. §24 remains a cloud effect.
`AClearSkyGain_OverflowsCompletely_BecauseThereIsNoDimmingToAbsorbIt` and
`OnClearWeather_TheWholeCavityOverflows_SoTheResidualIsGainMinusOne` pin the arithmetic (the latter
walking §22's own measured fractions through `CloudBaseAlbedo` to the residual), so a later "tidy-up"
cannot make the clear-sky case look intrinsically zero and silently switch this back off.

— and neither needs to know the other exists at draw time. `SnowGlareMathTests` asserts the partition
directly (no gap, no overlap, and the two summing back to the unclamped product), which is why that
file compiles `AlbedoCavityMath` alongside its own subject.

**Cheap by shape, not by tuning, and that is the whole reason it is a quad.** The expensive design for
a snow effect is per-cell: a `SectionLayer` subscribing to `MapMeshFlagDefOf.Snow`, a flag
`SnowGrid.CheckVisualOrPathCostChange` raises constantly during snowfall — exactly what §16's ledger
(issues #20, #60) says to cost before building. §24 needs none of it, because **§21's model is already
a whole-map areal mean** (`SurfaceBuildup` reads `TotalDepth / Area`). There is no per-cell
information in the quantity being drawn, so one uniform quad is not a compromise on the model, it is
its resolution. Per frame: one `Graphics.DrawMesh` against `MeshPool.wholeMapPlane`, no mesh
regeneration, no dirty flag, no allocation after startup, and the material colour written only when
the alpha actually moves.

**Altitude is load-bearing, and the obvious choice is wrong.** `AltitudeLayer.Weather` (31) — where
vanilla's own weather overlays draw — sits directly below `LightingOverlay` (32), so anything drawn
there is multiplied by the sky colour afterwards. For an additive pass whose purpose is to exceed
what that multiply can express, that is self-defeating. A Weather-altitude build was actually run
before this was noticed, and it is worth recording that it did **not** render nothing: it measured
ΔE 19.79, because an overcast daytime palette is a ~0.8 multiply and passes most of the addition
through. The failure is conditional rather than total — the attenuation tracks the palette, so it
bites hardest where the sky is darkest and glare would *fade as the deck thickened*, which is
backwards. `VisEffects` (33) is above `LightingOverlay` and below `FogOfWar`, the same pair §11a's
curtain needed. Staying below `FogOfWar` is right for glare specifically where it was wrong for the
aurora: an aurora is sky and is not hidden by a player's ignorance of terrain, but glare is light
bouncing off *ground*.

### The bug the offline tests could not catch

The first cut scaled the alpha by `CurSkyGlow` alone, on the reasoning that sky glow goes to zero
after dusk and would gate the effect off at night for free. **It does not.** §7 holds a night floor of
starlight, airglow and moonlight, and §21 *amplifies that floor over snow*
(`NightRadiance.FloorGlowFor` multiplies it by the same cavity gain). On a snowed-in overcast night
the harness read a live alpha of **0.0372** where the scenario pinned 0 — §21 was being paid for twice,
once through its multiplicative night arm and again through this additive one, in exactly the
conditions the night arm was built for.

The offline test suite passed throughout, because it asserted "night is zero" by feeding `skyGlow`
0 — a value that never occurs on a snowy map. `SnowGlareMath.DaylightAboveNightFloor` is the fix:
scale by `skyGlow - nightFloorGlow`, floored at zero. That is the *right* quantity rather than a
convenient one — the floor is precisely the light §21 has already amplified, so what remains above it
is precisely the light it has not. No daytime/night branch, no second copy of §6b's
`LightingSun`→`LightingMoon` threshold to keep in sync, and a continuous ramp through dusk.

### Live verification

`Tests/Scenarios/snow_glare.json`, latitude 55, day 5, Cinematic preset, full-map fresh snow (four
tiled `SetSnow` patches — the harness's per-call cell cap is 128×128 and the fixture is 250×250), Fog
at noon. `surface_cavity_gain` **2.2422**, `snow_glare_excess` **0.9732** — a 97% overflow, which is
the undrawable 1.92× of issue #90's table appearing as a measured number.

| capture | condition | `snow_glare_alpha` | median CIELAB ΔE vs off |
|---|---|---|---|
| `glare_overcast_noon_on.png` | Fog, noon, calibrated | 0.0532 | **5.13** — obvious |
| `glare_overcast_noon_strong.png` | Fog, noon, swept to 0.18 | 0.1595 | **14.87** — obvious |
| `glare_dusk_on.png` | Fog, 16:00 | 0.0315 | **3.00** — visible at a glance |
| `glare_night_on.png` | Fog, 01:00 | **0.0000** | **0.00** — 0.0% of pixels changed |
| `glare_clear_noon_on.png` | Clear, noon, §22 cover **0.0091** | 0.0042 | **0.38** — imperceptible |
| `glare_partly_cloudy_noon_on.png` | Clear, noon, §22 cover **0.2113** | 0.0106 | **1.13** — visible on close inspection |

**The two Clear rows are one measurement of the same ramp at two cloud fractions, and #134 is the
reason they are both here.** The Clear-noon capture in `snow_glare.json` happens to land on an almost
cloudless hour (§22 reads 0.0091), so it measures the *boundary step* described above rather than the
feature: 0.38 is the clear-sky cavity alone, and it is below this repo's shipping bar by design.
`snow_glare_partly_cloudy.json` exists because of exactly that — latitude 45, day 40, noon, the same
tile and hour whose 0.2113 fraction `cloud_cover.json` already pins, so the two scenarios cross-check
§22's clock as well as §24's response to it. `surface_cavity_gain` **1.1855**, `snow_glare_excess`
**0.1855** (identical, because Clear spends no dimming), alpha **0.0106**, ΔE **1.13**.

**1.13 is a real but modest effect, and issue #134 predicted the range before it was built** (ΔE
1–1.5, from a paper estimate of the residual ladder — the earlier verbal guess of "comparable to the
overcast case" was wrong by an order of magnitude and #134 corrected it in writing). It sits above
§20c's knowingly-inert 0.36 and alongside §19b's 1.48, so it ships; it is a fifth of the overcast
case's 5.13, which is the right ordering for a fifth as much cloud. The scenario also pins the
feature-off baseline the only way that means anything here: with §22 turned off, `cloud_cover_fraction`,
`snow_glare_excess` and `snow_glare_alpha` all return to exactly **0.0000** on the same frame, which is
the pre-#134 behaviour reproduced rather than approximated.

**The daylight ramp, surveyed rather than assumed** (`snow_glare_alpha` by hour, Fog, full-map snow):
16:00 **0.0315**, 17:00 **0.0200**, 18:00 **0.0071**, 19:00 **0.0000**. It fades out with the light and
reaches exactly zero before night rather than stepping off at a threshold — which is what
`DaylightAboveNightFloor` buys over a daytime/night branch.

**The night pair is the double-count guard's evidence, and it is deliberately a null result.** Both
frames are pixel-identical because §21 already amplifies the night floor multiplicatively there — the
snowy map is visibly not black, and none of that is §24's doing. A capture that showed *any*
difference at 01:00 would mean the bug found during this prototype's own bring-up had returned.

**On a polar snowfield** (`snow_glare_icesheet.json`, latitude 70, `Ice` terrain painted map-wide,
full-depth snow re-laid per scene, Fog). The cavity is saturated in all three — `surface_cavity_gain`
2.2422, `snow_glare_excess` 0.9732 — so what moves between them is purely the daylight term:

| scene | `sun_elevation` | `snow_glare_alpha` | median ΔE |
|---|---|---|---|
| `ice_winter_*` (day 52) | **4.3156°** | 0.0382 | **3.67** |
| `ice_equinox_*` (day 37) | **37.4193°** | 0.0532 | **5.08** |
| `ice_summer_*` (day 22) | **35.6844°** | 0.0532 | **5.08** |

**The alpha plateaus at 0.0532** once the sun is well up, because `DaylightAboveNightFloor` saturates —
`skyGlow` is already 1 and the only remaining variable is the floor. So a polar summer is no brighter
than an equinox as far as §24 is concerned, and the low-sun winter case is the only one that reads
differently. That is the ramp behaving as designed, not a survey that missed the peak.

**A prediction that was wrong, kept because it cost a wrong scene choice.** These latitudes were
picked expecting polar night: the textbook maximum solar elevation at latitude 70 at the winter
solstice is −3.44°, which would have made §24 exactly zero and the scene pointless. Measured, day 52
at latitude 70 puts the sun at **+4.32°** — RimWorld's day-of-year to declination mapping is not the
textbook one, and day 52 is not its solstice. This is exactly why CLAUDE.md says to survey with
`sun_elevation` and pin it next to the effect rather than reasoning about hours from first principles.

**The painted `Ice` terrain contributed nothing visible** and is retained only so the scenario says
what it means: full-depth snow covers the ground completely, so the terrain beneath it is never drawn.
What makes these frames a polar scene is the latitude, the season and the saturated buildup, not the
terrain def.

**Roof masking, measured by crop** (`glare_roof_noon_*`, zoomed onto the fixture's walled room):
inside the roofed interior median ΔE **0.00** with **0.0%** of pixels changed; on open ground in the
same frame pair, median ΔE **4.85** with **100%** changed. The mask is doing exactly what it claims,
and the two crops in one frame pair is the cheapest way to show it.

**THE INVERSION IS DRAWABLE, AND THE PRICE IS NOW MEASURED RATHER THAN GUESSED.** Comparing mean frame
colour, snowy clear sits at `rgb(162, 144, 130)`. At the calibrated strength the snowy overcast reaches
only `rgb(144, 132, 122)` — still *darker* than clear, so #90's ordering is **not** restored. At the
swept strength it reaches `rgb(169, 157, 147)` and **does** exceed clear, i.e. the inversion renders.
So the answer to the ticket's open question is a number: the effect needs roughly ΔE 15 to make
overcast visibly out-brighten clear, against a mod whose largest shipped effect to date is §20b
pollution at 6.79, and at that strength it reads as a bright hazy whiteout with visibly flattened
terrain contrast. An earlier accidental build at ΔE 19.79 read as milky haze outright.

That is the trade #90 asked to have quantified, and it is genuinely two-sided rather than a
disappointment: a whiteout *is* what standing on snow under a deck looks like, and flattened contrast
is §21's own stated physics (the cavity restores brightness and destroys contrast — "that asymmetry is
the whiteout"). Whether RimWorld's fixed exposure makes it read as bright or as washed out is the call
this section leaves open, with frames committed to `Tests/Screenshots/` so it can be made by looking.

### Performance

The standing cost is what matters here and it is not the aurora's profile. §11a is rare and
time-limited, so its per-frame cost amortises over how seldom it runs; snow glare on an ice sheet is
every daylight frame, all year. Profiled over 1201 frames:

**Two windows, because one of them would be a lie on its own.** `snow_glare.json`'s window profiles the
subsystem at full tilt — snowed in, overcast, noon, drawing every frame — which is the state a polar
colony sits in and nowhere near the state most maps are in. `snow_glare_gated.json` profiles the same
build on a bare map, where `HasBuildup` returns false and the postfix leaves immediately. Quoting only
the first would overstate the standing cost for almost every save; quoting only the second would be
marketing.

```
drawing every frame (snowed in, Fog, noon)
  Patch_SnowGlareDraw:Postfix   avgMsPerFrame 0.0696   maxMsPerFrame 3.6149
                                callsPerFrame 2.00     avgUsPerCall 34.84
                                0.418% of a 60 fps budget

gated out (no buildup on the map)
  Patch_SnowGlareDraw:Postfix   avgMsPerFrame 0.0008   maxMsPerFrame 0.0791
                                callsPerFrame 2.00     avgUsPerCall 0.41
                                0.005% of a 60 fps budget
```

**87× between the two, which is the whole argument for the gate order.** A map with no buildup pays
0.41 µs per call against 34.84 — two `TotalDepth` field reads and a return. Before `HasBuildup` existed
that map still walked the full weather classifier every frame to arrive at zero, so this is not a
micro-optimisation of an already-cheap path; it is the difference between the subsystem costing
something on every save in the game and costing something only where it can actually draw.

`callsPerFrame` 2.00 is `GameConditionManagerDraw` recursing into its parent manager; the identity
guard early-returns on one of the two, so the real work is ~70 µs on the pass that draws — the 34.84 µs
mean averages that against a no-op, per the harness's own note about call counts including early
returns.

**The mask made the drawing case dearer, and that is the trade being bought.** Before roof masking the
same window measured 0.0495 ms/frame at 24.79 µs per call; the mask adds a mesh lookup and swaps
vanilla's shared plane for our own geometry, taking it to 0.0696 at 34.84. That is the cost of not
washing glare across every roofed interior, paid only on maps that are actually drawing glare.

**Almost none of either figure is the draw call.** The cost is `SnowGlare.AlphaFor` walking
`WeatherDimming.CloudOpacityFor` — which `MapSky`'s header records as deliberately un-memoized — and
walking it **twice**, once via `UndrawableExcessFor` and again inside `NightRadiance.FloorGlowFor` →
`SurfaceBuildup.CavityGainFor`. That redundancy is an artifact of the night-floor fix and is the
obvious thing to remove (a `FrameStamp` memo, per issue #12's pattern) if this ever ships on.

`maxMsPerFrame` 3.6149 against a 0.0696 mean is worth noting rather than smoothing over: a max fifty
times the mean is a dropped frame, and it is most likely the mask's first build (or a rebuild after a
roof write) landing inside the window. If §24 goes past prototype that wants isolating before the memo
above, not after.

`callsPerFrame` 2.00 is `GameConditionManagerDraw` recursing into its parent manager; the identity
guard early-returns on one of the two, so the real work is ~50 µs on the pass that draws — and per
this repo's own rule about call counts including early returns, the mean-per-call figure understates
that. **Almost none of it is the draw call.** The cost is `SnowGlare.AlphaFor` walking
`WeatherDimming.CloudOpacityFor`, which `MapSky`'s header records as deliberately un-memoized, and
walking it **twice**: once via `UndrawableExcessFor` and again inside `NightRadiance.FloorGlowFor` →
`SurfaceBuildup.CavityGainFor`. That redundancy is an artifact of the night-floor fix and is the
obvious thing to remove (a `FrameStamp` memo, per issue #12's pattern) if this ever ships on.

### The roof mask (`OpenSkyMaskMath` / `OpenSkyMask` / `Patch_OpenSkyMaskInvalidation`)

**Named for the mask, not for §24, because §23b now draws through it too.** It was built here first
and the reasoning below is §24's, but "which cells can see the sky" is a property of the map rather
than of either effect, so both consumers share one cache, one set of roof-write hooks and one
rebuild. Its mesh charts UVs in **map space** (`x / width`, `z / height`) rather than 0..1 per quad,
matching `MeshPool.wholeMapPlane`'s own convention — invisible to §24, whose material carries no
texture, and load-bearing for §23b, which tiles one.

`SectionLayer_IndoorMask` draws between `Weather` and `VisEffects` (measured, per §11a's altitude
note), i.e. **below** §24 — so vanilla's own roof masking does not apply, and the first prototype
washed glare straight across roofed interiors that have no sky. Dropping below the mask to catch it is
not available: that is also below `LightingOverlay`, which is the fatal case above. So the mask has to
be geometry we build.

`OpenSkyMaskMath.UnroofedRuns` collapses each row's unroofed cells into maximal horizontal spans,
and `OpenSkyMask` turns those into one mesh. **Rows only, no vertical merge** — a 2-D merge would
compress an unroofed map from 250 quads to 1, but it is the kind of code that is wrong in exactly one
configuration nobody tests, and the mesh is rebuilt so rarely that a few hundred spare quads cost
nothing. A map with nothing roofed short-circuits to `MeshPool.wholeMapPlane`, the shared mesh vanilla
already keeps resident, so the common case builds nothing at all.

**Why keying on roofs is affordable where keying on snow was not.** This is the same per-cell precision
§21's shadow-fill arm is deferred for, and it escapes §16's ledger (issues #20, #60) on a single
distinction: that ledger is about `MapMeshFlagDefOf.Snow`, which `SnowGrid.CheckVisualOrPathCostChange`
raises constantly during snowfall. Roofs change when a player builds and otherwise never. Same
precision, a rebuild rate near zero.

Invalidation is a postfix on `RoofGrid.SetRoof` and `RoofGrid.RemoveRoofUnsafe` rather than a
`relevantChangeTypes` widening, because §24 is not a section layer and would have had to become one
purely to receive the notification. It is also strictly *cheaper* than the flag:
`Patch_ShadowRoofInvalidation`'s own header records that `GlowGrid.DirtyCell` raises `Roofs` alongside
`GroundGlow`, so a flag subscriber rebuilds every time a lamp toggles. Patching the two writers
rebuilds on roofs and only on roofs.

### The gates, and what a map with no snow pays

§24's draw hook runs once per frame for as long as a save is loaded, so the number that matters is
what a map with **no snow** costs — which is every map in most biomes, and every snowy map for most of
the year. `SnowGlare.AlphaFor` is therefore ordered by cost × selectivity rather than by the order the
physics reads:

1. **Feature flag** — one bool.
2. **Buildup** (`SurfaceBuildup.HasBuildup`) — two `TotalDepth` field reads, O(1) regardless of map
   size, and by far the most selective question available: no buildup means gain is exactly 1, which
   means the residual is exactly 0 whatever the sky is doing.
3. **Daylight** — a cached float on `SkyManager`. Skips the weather walk for every night frame on a
   snowed-in map, which on a polar colony is most of them.
4. **Weather** — only now, and it is itself a gate: `UndrawableExcessFor` returns 0 the moment §13
   reports no cloud deck, so Clear never reaches the arithmetic.

Before gates 2 and 3 existed, a snowless map walked the full weather classifier every frame to arrive
at zero. That was the entire measured cost of the subsystem on maps where it can never do anything.

## 23c. Daylight cloud shadows (`CloudShadowMath` / `CloudShadowOverlay`)

**Status: SHIPPED OFF** (`cloud_shadow`), alongside §23b. §25 was the third of the group and is now
shipped **on** — see its own status note.

**Problem, and it was found by looking at §23b rather than by planning.** Watching §23b's warm patches
drift over a twilit map, the natural description is "the sun is being shaded by clouds". That is the
*wrong* reading of §23b — inside its window the sun is below the horizon and there is no direct beam
left to shade — but it is a completely right description of what a broken deck does for the other
twelve hours of the day, and the mod did not have it. An additive-only pattern with no cloud drawn
above it is genuinely ambiguous: given light and dark patches, the eye reaches for the reading it has
ten thousand hours of. Rather than fight that reading, §23c is the effect it was reaching for.

So the two are **one phenomenon at opposite ends of the day**, and they share a field:

| sun | the deck is | §23c/§23b draws |
|---|---|---|
| above the horizon | an **occluder** | subtract light where the cloud is (alpha-blended black) |
| below the horizon | a **source** | add warm light where the cloud is (additive) |

**Same partition, so §13/§22 are not double-counted.** Those already darken the whole sky by the *mean*
cloud amount. What a flat colour cannot express is that the darkening is uneven, so this lane draws the
field's residual above its own mean, exactly as §23b does. Both ends of the coverage range therefore
draw nothing — and the overcast end is the one worth stating, because "more cloud is more shadow" is
the intuition a later change would follow: a solid deck shades the whole map evenly, and an even shade
is precisely §13's job.

**The low-sun fade is not decoration.** `DirectBeamFraction` is `sin(elevation)` — the beam's
illuminance on a horizontal surface — with a quadratic fade below 10°. A cloud shadow needs a direct
beam to block, and near the horizon most of what reaches the ground is diffuse skylight the deck does
not occlude sharply, so the shadow has to be nearly gone well before sunset rather than switching off
at it. That also hands over cleanly to §23b, which starts only once the sun is *below* the horizon,
leaving a wide band where neither lane draws and the sky is simply changing colour.

**Altitude is `VisEffects`, the same as §23b, and a shadow's natural home looks like it should be
lower.** Vanilla's own sun shadows draw far below the lighting overlay as part of the map mesh —
correctly, because they are cast *by* things *on* the map. A cloud shadow is cast by something above
everything on the map, so it must darken pawns, buildings and roofs alike, which means drawing after
them.

### Live verification

`Tests/Scenarios/cloud_layers.json`, latitude 45, day 40, Clear with §22's fraction forced to 0.35.
At noon (`sun_elevation` **56.72°**) `cloud_shadow_alpha` reads **0.1505** and the frame measures
median CIELAB ΔE **1.28** against the same frame with the lane off — *visible on close inspection*,
and the honest verdict is that it is **too subtle**. The residual peaks around 0.65, so the strongest
patch is drawn at alpha ~0.10 over a fully-lit map, where the eye is comparing against a bright
surround. `ShadowAmplitude` (0.18) is the knob and the harness sweeps it; a value roughly double this
is where the next look should start.

Pinned nulls, which are the claims frames alone cannot make: `cloud_shadow_alpha` reads exactly
**0.0000** at dusk (-1.17°, where §23b is drawing 0.0976 instead) and exactly **0.0000** under a solid
overcast at noon. The lanes hand over cleanly and neither doubles the other.

## 25. The drawn cloud sheet (`CloudSheetMath` / `CloudSheetOverlay`, issue #138)

**Status: SHIPPED ON** (`cloud_sheet`), and it is the only one of the three prototype lanes that is.
It shipped *off* while it was the tiled field, whose frames argued against the approach rather than
for it — that verdict is kept below rather than deleted, because it is what the bounded-sheet
redesign was answering, and issue #138 asked for exactly that: "answer *does drawn cloud read at all
from this camera* before designing anything larger". The bounded version's frames answer yes.

**It is on because of what it is, not because it measured well.** §23b and §23c adjust light a player
attributes to the weather; this one is the mod visibly drawing clouds, which is the thing a player
would call the feature. So it gets a **settings checkbox of its own** ("Visible clouds"), nested
under "Partial cloud cover" as a **sub-toggle of §22** — the same relationship the `- N% cloudy`
label has, for the same reason: §22 is the master for *does this mod have an opinion about cloud at
all*, and drawing a deck over a player who switched that off would be the mod arguing with them. The
gate is load-bearing rather than bookkeeping, because coverage here comes from §13's weather deck as
well as §22's Clear-day fraction (`CloudFractionFor`), so without it a rainy day would keep growing
sheets with partial cover off. Both flags are checked in `CloudLayers.SheetAlphaFor`, which returns 0
and skips the draw call entirely — off is the pre-feature baseline exactly, for the harness and for
the player.

**The §13 double-count is now a shipped cost rather than a prototype's.** See the note below on
`SheetAmplitude` 0.35; it is the first thing to fix.

**What it is.** The other two lanes draw *illumination* and stop at the ground, below `FogOfWar`. This
draws *sky*: cloud between the camera and the map, above `FogOfWar` for the same reason §11a's aurora
is (a cloud is not hidden by a player's ignorance of the terrain beneath it). Alpha-blended rather
than additive, because cloud occludes.

**It is bounded sheets, and the version before it was a tiled field.** That first cut stretched one
tiling noise texture over the whole map, and it was measured before it was replaced: mottled haze at
partial cover, and — a tiling field at full coverage being uniform — a flat grey veil at full cover,
**ΔE 13.99 with every pixel changed**, reading washed out rather than overcast. It worked least well
exactly where it drew most. The tiling was doing damage on its own: *a repeat is a rhythm, and a sky
does not have one.*

So §25 is now several **bounded cloud sheets** — §11a's own arrangement (slot materials, one quad
each), with the one difference that these *move*: an aurora's sheets stand still and shimmer, where a
cloud's whole character is that it goes somewhere. What that buys is not cosmetic:

- **No repeat to find**, because nothing tiles. A sheet's alpha reaches zero inside its own quad
  (`BlobCoreFraction`), so it has an edge rather than a seam, and the texture is `Clamp` not `Repeat`.
- **Coverage is a COUNT** (`CloudSheetLayout.SheetCount`, `ceil(fraction × 12)`) — more cloud is more
  clouds, which is what more cloud looks like out of a window. Full cover is a dozen overlapping
  sheets rather than a uniform slab.
- **Motion is not rigid.** Speeds vary by up to half (`SpeedVariation`), so the sky cannot translate
  as one picture — the failure `AuroraFieldRegistry.Contour` already records. Headings vary only a few
  degrees (`CrossDriftFraction`), because a cloud field is one air mass.
- **Sheets enter and leave off-map.** The travel span is the map plus a whole sheet at each end, so
  the wrap at the end of a crossing happens where nothing can see it — which makes "remove it and
  start another" free, with no live-cloud list, no spawn schedule and nothing to persist. The sky at
  tick N is a pure function of N, which is also what makes it screenshotable.
- **The shape is baked once, ever.** A bounded sheet's shape does not depend on coverage, position or
  sun colour, so the 2×2 blob atlas is filled in a static constructor during load. That deletes the
  7 ms main-thread bake the tiled version paid every time the cloud fraction moved.

**Overlapping sheets read as thicker cloud, capped.** Ordinary alpha blending converges on the sheet's
own colour, so two stacked sheets look exactly like one slightly more opaque one — which is wrong,
because there is physically more cloud in that column. Asking the GPU would need a framebuffer read
back per frame; asking the *layout* is free, since there are at most twelve of them and their
positions are already known. `CloudSheetLayout.OverlapDepth` sums the circle overlap with every other
sheet, weighted by that sheet's own alpha, and `CloudSheetMath.OverlapBoost` turns it into a capped
multiplier on both alpha and brightness. The cap is the point: unbounded accumulation would make a
busy sky a white slab, which is the tiled version's failure reached from the other direction.

**One cloud type.** The atlas holds four shapes of the same character; variety comes from shape,
mirroring, size, speed and position. A second *type* — thin high cirrus against fat cumulus — would be
a different shaping curve in `FillBlobAtlas` plus a second atlas, and is deliberately not attempted.

### All three lanes draw the same sheets

**They did not, for a while, and that is the bug this section exists to record.** §25 moved to bounded
sheets while §23b and §23c were still keyed on the tiled field, which put *two different cloud
patterns on one screen*: the shadow patches and the drawn clouds disagreed about where the clouds
were. The whole "one field" premise had quietly stopped being true.

All three now iterate `CloudSheetLayout`'s placements through `CloudSheetDraw`, and they draw them two
different ways depending on whether a roof stops them:

| lane | drawn as | masked | altitude |
|---|---|---|---|
| §25 cloud | its own quad | no — cloud is above the roof | above `FogOfWar` |
| §23c shadow | the open-sky mask's geometry, blob placed through its map-space UVs | yes | `VisEffects` |
| §23b underlight | the same | yes | `VisEffects` |

**The UV route is what makes a bounded shape maskable.** A quad cannot be clipped to an arbitrary set
of cells in one draw call; the mask's mesh already *is* that set of cells, and its UVs chart 0..1
across the map (see §24's note on that convention), so positioning the blob through
`CloudSheetLayout.UvTransform` puts the cloud where it belongs and leaves roofed cells simply not
drawn. `OverlapBoost` feeds all three, so a thick patch of sky, the dark patch under it and the warm
light it bounces are one number rather than three.

**It cost rotation.** A texture transform can translate, scale and mirror; it cannot rotate. Rather
than keep rotation for §25 alone — a cloud whose shadow sat at a different angle to itself is worse
than one that never rotates — rotation became mirroring, which both paths express. Four atlas shapes
× four flip combinations is sixteen silhouettes, re-rolled per crossing.

**Vacuum and skyless maps.** All three lanes ask `MapSky.HasSky` and `MapSky.SkyBlackedOut` before any
arithmetic, and every pure core takes `inVacuum` as its last required parameter and early-returns on
it, per `Vacuum.cs`'s convention. A cavern, a pocket map and an orbital habitat all draw nothing.

**Not a residual, unlike the other two, and that is deliberate.** A drawn cloud is the object, not an
adjustment to a flat approximation of it: an overcast sky should come out *covered*, not
uniform-and-therefore-invisible. The honest cost, recorded rather than hidden, is that over a solid
overcast the sheet and §13's flat dimming are both rendering the same deck, so the map is darker than
either alone intends. That double-count is why `SheetAmplitude` is only 0.35, and now that the lane
ships on it is a cost every player pays under overcast rather than an opt-in prototype's — the first
thing to fix here, most likely by feeding §13 a reduced opacity while the sheet draws, so the two
partition the deck the way §23b and §23 partition the underlight.

### Live verification

| capture | condition | `cloud_sheet_alpha` | median ΔE | p90 ΔE |
|---|---|---|---|---|
| `cl_noon_sheet.png` | Clear, cover 0.35 (5 sheets), noon | 0.3500 | **0.00** | **7.52** |
| `cl_overcast_noon_all.png` | Overcast (12 sheets), all three lanes | 0.3346 | **4.46** | 23.19 |
| `cl_dusk_underlight_sheet.png` | dusk, with §23b | 0.0422 | 4.28 | — |

**THE MEDIAN IS THE WRONG STATISTIC FOR THIS LANE, and that is worth stating rather than quietly
switching numbers.** The parent CLAUDE.md's rule — median per-pixel CIELAB ΔE, never channel means —
exists because a *map-wide* change is what every earlier subsystem made, and a mean cancels. A bounded
cloud covering half the frame is the case that rule does not cover: half the pixels are untouched by
construction, so the median reports 0 while the cloud is plainly on screen. The noon frame above is
exactly that — median 0.00, p90 **7.52**, 48.9% of pixels changed. **Quote a percentile for a lane
that draws objects rather than weather**, and say which; the median stays right for §23b and §23c,
which do change the whole open-sky map.

**It reads as cloud now.** The noon frame is a soft-edged mass over the right of the map with clear
sky beside it — a boundary, not a texture. At full cover the twelve overlapping sheets measure ΔE
4.46 rather than the tiled version's 13.99, and look like a covered sky rather than a grey veil.

### Shipping it on: the default and the §22 gate (`Tests/Scenarios/cloud_sheet_default.json`)

Two claims that only a live run can make, because both are about what a player who touches nothing
sees. The scenario never issues a `SetFeature cloud_sheet` at all — the reading comes from whatever
the flag rests at, which is the shipped default — and then switches **partial cloud cover** off to
check that the master carries the sheet with it.

| capture | condition | `cloud_sheet_alpha` | median ΔE | p90 ΔE | pixels changed |
|---|---|---|---|---|---|
| `csd_clear_default.png` | Clear, cover 0.35, noon, nothing set | **0.3500** | 0.00 | 2.55 | 31.0% |
| `csd_clear_cover_off.png` | the same, §22 off | **0.0000** | — | — | — |
| `csd_overcast_default.png` | Overcast noon, sheet lane only | **0.3346** | 0.00 | 1.79 | 30.3% |
| `csd_overcast_cover_off.png` | the same, §22 off | **0.0000** | — | — | — |

**The Overcast zero is the pin that matters**, and it is worth saying why the Clear one is nearly
free: with §22 off, `CloudFractionFor`'s Clear arm already reads 0 by itself, so that row would pass
without the gate existing. Overcast coverage comes from §13's weather deck, which §22's switch does
not touch — a sheet still drawing over a rainy map for somebody who turned partial cloud cover off
is the actual bug, and only that row catches it.

**LEAD WITH THE WEAK NUMBER: p90 2.55 here against the 7.52 the table above measured under the same
weather, fraction and hour.** The alpha is identical in both (0.3500), so nothing about the lane's
strength changed — what differs is *where the sheets were*. Placement is a function of the absolute
tick, and at this run's tick the deck sat across the top third of the camera with the colony in clear
sky below it, where the earlier capture caught a mass over the middle. An amplified difference of the
Overcast pair (`csd_overcast_diff24x.png`, ×24) shows exactly that: soft-edged cloud across the upper
third, black everywhere else.

That is a property of the lane rather than a flaw in the run, and it is the honest caveat on **any**
single-frame ΔE for §25: a bounded object that moves gives a different number every tick, so these
figures bound how strong the effect is *somewhere in frame*, not how strong it is. Judging the deck
as a whole is what `cloud_sheet_lapse.json` is for.

**A real bug the redesign introduced, found by measuring rather than by reading.** The first bounded
build still scaled a sheet's alpha by the cloud fraction, carried over from the tiled version where
one stretched field had to express coverage as opacity. With coverage now a *count*, that counted it
twice: a 0.35-covered noon sky rendered at median ΔE 0.00 with p90 0.00 — visible only as a 1-in-255
lift of the frame mean. `SheetAlpha` now takes the fraction as a gate and nothing else, and
`ASheetsOwnOpacityDoesNotScaleWithHowCloudyItIs` pins it.

### Performance

The bake is gone as a per-frame concern: the atlas is filled once in a static constructor during load,
because a bounded sheet's shape depends on nothing that changes. The tiled version paid **7.07 ms** on
the main thread every time the cloud fraction moved a quantum, and **28.43 ms** before its resolution
came down — both measured by `Tools/CloudPreview`, and both now historical.

What remains is up to `MaxSheets` (12) draw calls and material writes per frame, of which only the
on-map ones are issued (`CloudSheetLayout.OnScreen` — sheets spend a real share of each crossing
outside the map).

**Now profiled in place, which shipping it on made compulsory rather than optional.**
`cloud_sheet_default.json`, 58 frames under Dubs Performance Analyzer, five sheets on a Clear map and
twelve under Overcast:

| row | avg ms/frame | max ms/frame | calls/frame | µs/call | share of a 60 fps frame |
|---|---|---|---|---|---|
| `Patch_CloudLayersDraw:Postfix` | 0.213 | 3.67 | 1.97 | 108 | **1.28%** |

Read it with three caveats, all of which make it a ceiling rather than an estimate. The analyzer
transplants timing calls into every patched method, so the absolute figure carries that overhead. The
hook is shared by all three lanes and only §25 was live, so this is the whole cloud draw path. And
**the max is 17× the mean and has not been attributed** — the window spans the load, a weather switch
and the sheet count going from five to twelve, any of which could own it, and the per-patch table
cannot say which. That is the number to isolate first if this lane is ever suspected of a hitch;
quoting the mean alone would be exactly the mistake the parent CLAUDE.md warns about. The game was
paused throughout, which does not affect a render-path cost but does mean nothing here speaks to
tick-driven work; there is none in this lane.

## 27. Vector light sources (`VectorLightMath` / `VectorLightOverlay`, issues #48 / #103)

**Problem.** Artificial light has no direction. `Verse.Glow.ComputeGlowGridsJob` runs a Dijkstra
flood per glower over an 8-neighbour lattice with 100/141 fixed-point costs, and the distance it
accumulates is **geodesic** — the length of the shortest path *around* the walls, not the straight
line. Three consequences, all visible:

- Light bends around corners. A lamp behind a wall smears onto the far side.
- A diagonal step is refused only when *both* flanking cardinals are blockers (`case 4`–`case 7` of
  its neighbour switch), so a single diagonal gap leaks.
- The grid records how *far* light travelled and never which *direction* it came from, so nothing
  downstream can draw the dark side of anything. This is why issue #48 sat open from the start: a
  pawn beside a brazier at midnight is lit on all sides and throws nothing.

What was wanted instead is the question a photon would ask — from where the light is, what can it
*see*? Everything visible is lit, everything else is dark, and the boundary between them is a
straight line through a corner rather than a stairstep along a lattice. One mechanism then produces
all three target behaviours at once: a widening wedge through a doorway, a hard shadow behind a
rock, and firelight spilling out of a window.

**Approach.** A visibility polygon per emitter, cast from the light's own cell centre.

1. `SilhouetteSegments` turns the blocker cells within the light's reach into the **outline** of the
   blocked regions: edges shared between two blocker cells are deleted, and collinear spans on the
   same grid line are merged. A twelve-cell wall run comes back as four segments, not forty-eight.
2. Three rays per segment endpoint, at θ−ε, θ and θ+ε. The middle ray stops *on* the corner while
   its neighbours slip either side of it, and that pair **is** the shadow edge.
3. A base ring of 48 evenly spaced rays, so an unobstructed light is still round (7.5° per step puts
   the chord 0.03 cells inside the true circle at radius 14).
4. Sort by angle; the polygon is the fan. `BuildMesh` emits it as a triangle fan with a per-vertex
   radial texture coordinate.

The blocker set is vanilla's own — `ThingDef.blockLight` on the edifice, which is exactly what
`Verse.Building` writes into `GlowGrid`'s `lightBlockers` on spawn and despawn. Issue #48 names the
failure this avoids: a drawn shadow appearing across a wall the glow grid itself passed through.
Asking the same question of the same grid makes that disagreement impossible rather than unlikely.
The one deliberate exception is the light's **own** cell, treated as open even if something on it
blocks light, so a wall-mounted lamp is not sealed inside its own occluder.

**Suppression is half the feature, and it is the risky half.** Without it both models draw at once
and vanilla wins where it matters: its flood has already put light in every cell around the corner
that §27 just carved a shadow into, so every shadow fills back in from underneath. There is no
additive trick that removes light, so `Patch_VectorLightSuppress` zeroes the **RGB** of
`SectionLayer_LightingOverlay`'s vertices and leaves the **alpha** alone. That split is what makes it
safe: RGB is the artificial glow averaged from `VisualGlowAt`, alpha is the sky-cover term §7b owns.
Zeroing RGB puts every cell into the state an *unlit* cell already has in vanilla — an existing,
well-defined state rather than a novel one — so §7b's occlusion, §7c/§7d's falloff, §9's wash and the
sky colour all keep working untouched.

**Gameplay light is untouched.** `map.glowGrid` is never read, written or invalidated by the
suppressing half. `GroundGlowAt` / `PsychGlowAt` / `VisualGlowAt` return what they always did, so
plant growth, work speed, mood, `StatPart_Glow`, `DarklightUtility`, unnatural darkness and every mod
reading them see no change. §27 is a render, which is the whole reason it is allowed to be this
opinionated: being wrong here costs a look, not a save.

**The two things the obvious choice got wrong.**

- **Brightness travels as a texture coordinate, not a vertex colour.** The pass must be additive, and
  §23b's header records the finding that settles it: nothing in this codebase has ever asked
  `ShaderDatabase.MoteGlow` to honour a vertex colour, while §11a and §23b both put real structure
  through it as a *texture*. So the falloff curve is baked into a 1-D gradient in the **alpha**
  channel — where `AuroraCurtain` writes its own intensity — and the mesh carries only a radial `U`.
  It is also better geometry: `U` is distance/radius and distance is linear in position along a ray,
  so the GPU reproduces the curve **exactly**, where a ring-subdivided fan only approximated it at
  six times the vertices. The residual error is across a wedge, not along one: a point on the chord
  between two rays 7.5° apart is 0.2% nearer the light than its interpolated `U` claims.
- **Level is anchored at vanilla's own 0.5 artificial cap.** At full strength the first live A/B
  washed a 14-cell room out completely. An additive pass has neither vanilla's compositing under the
  sky multiply nor `GroundGlowAt`'s clamp of ordinary artificial light to 0.5, so a torch delivered
  visibly more light than the same torch does in vanilla. §27 is a change of *shape*; if brightness
  moved as well there would be no way to read which of the two produced the difference on screen.

**Falloff keeps vanilla's curve** — `lerp(1 − d/r, 1/d², 0.4)`, lifted from `SetGlowFromDist` — but
evaluated on a **euclidean** distance rather than a geodesic one. This is the subsystem's real
gameplay-adjacent consequence and belongs in the release notes rather than being discovered: cells
vanilla lit by a path bending around a corner now get nothing, so indirectly-lit rooms are genuinely
darker. That is the feature working, and it is also the most likely thing to need a compensation
knob before this is comfortable to live with.

**Daylight.** `DaylightScale` fades a light against the sky it competes with, keyed on whether the
sky *reaches* it rather than on `CurSkyGlow` flat. Keying on the global value puts every indoor lamp
out at noon — the one case where vanilla's lamp is most clearly visible, since a roofed cell renders
at a fraction of the sky and the lamp is what lifts it back. §7c's `NativeSkyFalloffGrid` already
answers "how much sky reaches this cell" properly and is the principled upgrade; the binary roof test
is the prototype's version of it.

**Invalidation, and why it is not a `MapMeshFlagDef`.** `GlowGrid.DirtyCell` raises `Roofs` *and*
`GroundGlow`, which §16 measures as the most frequently raised flag in the game — a flag subscription
would rebake every polygon on the map on every lamp toggle. So the four *actual writes* are patched
instead (`RegisterGlower`, `DeRegisterGlower`, `LightBlockerAdded`, `LightBlockerRemoved`), the same
conclusion `Patch_OpenSkyMaskInvalidation` reached for §24. Roster staleness and geometry staleness
are kept as separate states for the same reason: registering an emitter changes *who* is lighting the
map, while a blocker write changes the *shape* thrown by the handful of lights that can see that
cell. There is no timer anywhere — issue #48 states the rule and §16 has the measurement behind it.

`VectorLightField` reads `GlowGrid`'s own `litGlowers` and `litTerrain` rather than mirroring
registration into a private collection, so the roster is vanilla's answer by construction and the
patches shrink to setting one bool. `litTerrain` is **not** optional: suppression is total, so
glowing terrain §27 did not know about would go black rather than merely unimproved.

**Rejected.**

1. **Additive polygons on top of vanilla's render.** Nothing can regress and it is the fastest thing
   to look at, but shadows never actually get dark, because vanilla already lit those cells.
2. **Rewriting the lighting overlay's RGB from a raycast field instead of drawing our own geometry.**
   That lattice is one sample per cell corner plus one per centre, so every shadow edge smears over a
   full cell — which is the resolution §27 exists to escape.
3. **One rectangle per blocker cell rather than an outline.** Both describe the same obstruction, but
   a per-cell rectangle set has interior edges where two wall cells abut, and a ray aimed at the
   corner where four of them meet can slip *between* them on a rounding error: a one-pixel spike of
   light through a solid wall, appearing and disappearing as the camera moves. Deleting shared edges
   removes that by construction rather than by epsilon.
4. **A ring-subdivided fan carrying brightness in vertex colours.** Superseded on both counts above.
5. **Fading on global `CurSkyGlow`.** Puts every indoor lamp out at noon.

**Verification.** Offline, 1879 tests pass, and two of them are the ones that matter — geometry has
two failure modes no per-value assertion catches. `TrianglesTileThePolygonExactlyOnce` sums the
triangle areas against the polygon's own fan area, catching overlap (which on an additive pass
doubles the light where faces meet — §17 shipped that bug once) and gaps in one number.
`EveryNonDegenerateTriangleWindsTheSameWay` catches a flipped face, which on a backface-culling
top-down camera renders *nothing at all* while every numeric probe still reports healthy geometry.

`Tools/VectorLightPreview` rasterises the same shipped core offline in about a second against three
hand-authored layouts, which is how the geometry was iterated before any of it was booted. It earned
that in the first sitting: the doorway scene appeared not to work, and the geometry was already
correct — the light had been placed 10.5 cells from the door on a radius of 14, leaving under four
cells of reach beyond it. A scene that starves an effect of range is indistinguishable from a broken
one.

Live A/B in `vector_light_door.json` and `vector_light_blockers.json`, pinned on measured values
(`vector_light_shadow_fraction` 0.280 and 0.078 respectively). Median CIELAB ΔE **within the region
the effect touches** is **4.12** (door) and **4.11** (blockers), p90 6.48 and 5.72. Whole-frame
median is **0.00** for both — this is the bounded-effect case §25 documents, where only 6.6% and 8.6%
of the frame changes at all, so a whole-frame median is the wrong instrument and a percentile or a
masked median is the right one.

**Ships off** (`vector_lights`, registered with `defaultEnabled: false`). Off reproduces vanilla
exactly, which matters more here than for most flags precisely because the feature has a suppressing
half: with it false, `Patch_VectorLightSuppress` returns before touching the lighting overlay, so the
baseline frame is the real pre-feature render rather than a picture of the lights being missing.

### Soft edges — a source with a size (`vector_light_penumbra`)

Everything above treats each emitter as a *point*, and a point is the only thing that casts a
perfectly hard shadow. Nothing in a colony is one: a torch, a standing lamp and a campfire all occupy
about a cell, so every shadow they cast has a **penumbra** — a band at its edge where a receiver can
still see part of the source. Phase 2 gives the emitter a radius (`DefaultSourceRadius`, half a cell
for all of them, which is not worth a per-def lookup) and draws that band.

**The shape, and why the wedge has bands.** With the source a disc of radius `s` and the occluding
corner at distance `d0`, similar triangles put the penumbra's width at distance `d` at `s(d − d0)/d0`,
so its *angular* half-width is `s(d − d0)/(d0·d)` — zero at the corner, asymptotic to `s/d0` far
away. That radial dependence is the whole physical content of the model, and it is what rules out the
obvious implementation. A single triangle fanned from the light gives a wedge of *constant* angular
width, wrong by a full source width at every distance, and wrong in the worst available place: it
softens a shadow exactly where it meets the wall casting it and is genuinely sharp. So each wedge is
subdivided into `PenumbraBands` radial bands (4; eight is not distinguishable in the preview) with the
half-width evaluated per band, putting a piecewise-linear approximation through that curve.

**The ramp across the band is an S-curve, not a line.** Sliding a straight occluding edge across a
disc does not uncover it evenly — it uncovers a circular segment, area `arccos(p) − p√(1−p²)` over π.
A linear ramp passes both endpoints and still leaves a visible crease at each end of the band, where
the gradient meets flat light and flat shadow at an angle rather than tangentially, reading as two
faint extra edges in place of the one hard edge it was supposed to remove.

**It only ever adds light, which is a deliberate error.** A real penumbra straddles the geometric
boundary: the lit side should dim as much as the dark side brightens. This pass is additive, so it
can put light into the shadow and has no way to take light back out of the lit region, and the
alternative — rebuilding the fan so its boundary sits at the umbra instead — would make every shadow
*wider*. §27's standing risk is that indirectly-lit rooms come out uncomfortably dark, so of the two
available errors, reaching half a band too far into the shadow is the one that moves the safe way.
The visible softening is the same either way, and the fan's own vertices come out untouched vertex
for vertex, which `ASourceRadiusAddsWedgesBeyondTheFanAndLeavesTheFanAlone` pins.

**No shader, and not for want of one.** This was carried on epic #145 as blocked on a custom shader,
and the toolchain half of that is now resolved — Unity 2022.3.35f1 does build a bundle RimWorld
loads, verified end to end on this box. The feature turns out not to need it. `falloff(u) · ramp(v)`
is **separable**, so one bilinear sample of a 2-D gradient reproduces the product *exactly*, leaving
a fragment program nothing to compute; a shader would have bought no fidelity and cost a compiled
binary asset per platform, against §11a's standing "this repo ships no binary assets". What the
toolchain being live does change is that the next thing to want a shader is no longer blocked, and
that "we cannot build one" has stopped being a reason.

That gradient is also what makes the flag cheap. Its **first row is the falloff curve unmodified**,
so the off state is a source radius of zero and nothing else: no wedge geometry is emitted, every fan
vertex already carries `V = 0`, and the draw samples that first row. Off is phase 1's mesh and phase
1's texture lookup — the same objects, not a preserved copy of the code that used to build them —
which is what `PenumbraGradientFirstRowIsTheFalloffCurve` and
`ASourceRadiusOfZeroLeavesTheHardEdgedMeshUntouched` are for.

**Rejected here.** A *second* additive pass for the soft band (doubles the draw calls to compute
something the first pass's texture already carries). Vertex colours for the ramp (`MoteGlow` ignores
them — the finding `CloudUnderlightOverlay` records). Clamping the wedge against other occluders
further out (a penumbra grazing a second obstruction is a sub-cell error at the far end of a band
already half a source wide, and the clip would cost a second ray cast per band per corner).

**Verification.** Twenty offline tests, of which two carry the geometry: wedges must wind *with* the
fan — they open clockwise or anticlockwise depending on which side of the corner the shadow falls on,
so their index order has to flip with them, and a flipped face renders nothing at all on a
backface-culling top-down camera while every numeric probe stays healthy. That test caught a real
inversion on the first run. The fan must still tile the polygon exactly once, which needed
`LightMesh.FanTriangleCount` to even state, since the wedges deliberately lie *outside* the polygon
and would otherwise read as overlap. `Tools/VectorLightPreview` grew a hard-vs-soft pair per scene,
both arms from the same call with a different source radius.

### What publishing the vanilla arm showed, and the level it corrected

`vector_light_penumbra.json` captures **four** arms from one scene rather than the two an A/B needs:
vanilla's flood, the mixed case, phase 1's hard edges, phase 2's penumbra. The soft-edge measurement
is still taken between the last two, since holding everything else constant is what isolates the
penumbra from the mechanism drawing it. The extra arms exist because a two-arm A/B is blind to
anything §27 does to *both* of its frames — which is how the overall level ran bright for a whole
phase without anything catching it.

**`DefaultStrength` was 0.5 by argument and is now 0.35 by measurement.** The old value anchored on
vanilla's own `GlowGrid.GroundGlowAt` 0.5 cap, which is a reasonable-sounding derivation and came out
about **3 L\* too bright**: a lit room read mean L\* 17.09 against vanilla's 14.02 on the same
scene. The additive term is linear in that constant, so with the room's ambient floor taken from the
darkest fifth of the shadowed frame it solves directly — `0.5 × (vanilla's contribution ÷ ours)` =
0.3534. Measured back at 0.35:

| arm | lit room mean L\* | vs vanilla | room beyond the doorway |
|---|---|---|---|
| vanilla | 14.02 | — | 9.01 |
| mixed (suppression off) | 20.23 | **+6.21** | 9.52 |
| hard edges @ 0.35 | 14.39 | **+0.37** | 8.92 |
| soft edges @ 0.35 | 14.41 | +0.39 | 9.00 |

**The consequence is worth stating plainly, because it is not flattering.** Against vanilla, all of
§27 now measures masked median CIELAB ΔE **1.62** — down from 4.02 at the old level. That is not a
regression; it is the discovery that **most of what phase 1 measured was brightness rather than
shape**. 1.62 is the shape change on its own, which is what §27 claims to be. Phase 2's soft edges
likewise move from 1.80 to **1.38** for the same reason: a dimmer light has less contrast at its
shadow edges to soften. Both still clear the repo's ΔE-1 floor, but with less room than before, and
anyone raising the level again should expect both numbers to rise with it and should not read that
rise as the subsystem having improved. The level is linear and predictable if it is ever revisited:
0.30 → −0.88 L\*, 0.40 → +1.52, 0.45 → +2.60.

The far room also changes character at the corrected level. At 0.5 it came out *brighter* than
vanilla (8.95 → 9.10) and appeared to contradict the epic's headline risk. At 0.35 hard edges land
at **8.92 against vanilla's 9.01** — the predicted darkening finally showing, and small. Phase 2
brings it back to 9.00, because the penumbra restores at the shadow edges some of what the hard
boundary cut. That is a real argument for soft edges being on by default and not merely prettier.

### The mixed case (`vector_light_suppress`)

Epic #145 rejected "additive polygons on top of vanilla's render" on the argument that shadows never
actually get dark, because vanilla has already lit those cells. `vector_light_suppress` makes that a
photograph rather than a claim: with it off, `Patch_VectorLightSuppress` returns before touching the
lighting overlay and our polygons are drawn over vanilla's untouched flood.

It confirms the rejection twice over. The room goes to L\* **20.23**, half again as bright as
vanilla, because two full lighting models are now summed — and the shadows, though visible, never
reach dark, so the frame reads as a brightness increase with some structure in it rather than as
directional light. Masked median ΔE against vanilla is **7.34**, the largest number anywhere in this
section and the least desirable.

The flag is not only a demo. The epic asks for the suppressing half to be droppable independently of
the polygons — it is the risky one, since with it on anything §27 does not know about goes *black*
rather than merely unimproved — and this is that escape hatch, now switchable rather than hypothetical.

### The crossfade (`vector_light_blend`) — the mixed case, toned down

The mixed case fails because it **sums** two complete lighting models. Keeping the arrangement but
scaling both is a different proposition, and a better one. `vector_light_blend` keeps a fraction of
vanilla's flood underneath (`DefaultVanillaFloor`, 0.5) and drops our own contribution by the same
fraction, so the floor is a *redistribution* rather than an addition: at 0 it is §27 exactly, at 1 it
is vanilla exactly, and in between the overall level barely moves while the **shape** crossfades from
one model to the other.

| arm | lit room mean L\* | vs vanilla | deepest shadow L\* | room beyond the doorway | ΔE vs vanilla |
|---|---|---|---|---|---|
| vanilla | 14.02 | — | 4.20 | 9.01 | — |
| mixed, summed (rejected) | 20.23 | +6.21 | 4.20 | 9.52 | 7.34 |
| **crossfade @ 0.5** | **14.18** | **+0.17** | **3.54** | **9.00** | **1.04** |
| full §27, soft edges | 14.41 | +0.39 | 2.81 | 8.92–9.00 | 1.65 |

The shadow column is the one that says what this buys. Vanilla has no real shadow at all (4.20 is
just its dimmest corner); full §27 drives it to 2.81; the crossfade lands at **3.54**, between them
by construction. So a shadow is *dim rather than black*, and — the part that matters for the epic's
standing risk — **no room goes dark because §27 could not see into it**: the room beyond the doorway
reads 9.00 against vanilla's 9.01. Every cliff in §27's behaviour becomes a slope, and none of it
depends on the polygons having a complete picture of what emits light.

**It is not a max, which is what it wants to be.** The right composition is `max(vanilla, ours)` per
cell. That is not degenerate, and the reason is worth stating: vanilla's falloff runs on **geodesic**
distance, so in a beam through a doorway its light has travelled the long way round and arrived
dimmer than our straight-line value — a max would therefore take *our* beam exactly where we have
something to say, and vanilla's floor everywhere we do not, with no compromise in either place. The
crossfade instead dims both everywhere, which costs beam contrast that a max would have kept.

Writing it as `vanilla + max(0, ours − vanilla)` makes it expressible on an additive pass, since that
is what our pass already is. What it needs is a per-vertex "how much did vanilla deliver here"
channel so the subtraction happens per fragment. `MoteGlow` has no way to carry one: vertex colour is
ignored (`CloudUnderlightOverlay`'s finding), and both UV channels are already spent on the falloff
and the penumbra ramp. **This is the first thing in §27 that genuinely needs the custom shader**, and
as of 2026-08-18 nothing stands in its way: all three Unity build modules are installed and all three
bundles build clean and stamped (see the phase 2 note above), so the max is now an implementation
task rather than a toolchain one.

**Caveat on the floor's value.** At 0.5 the crossfade measures ΔE **1.04** against vanilla, which is
barely over the repo's own "under 1 is not shipped" line. It is safe and it is subtle, and those are
the same fact. A lower floor buys back contrast at the cost of the safety — 0.3 would sit roughly
two-thirds of the way toward full §27 — and the constant is the only thing that needs changing.

**Ships on** whenever §27 itself is on, and the deciding argument is **compatibility rather than
taste**. §27 knows about exactly what vanilla's `GlowGrid` tells it — registered glowers and glowing
terrain — which covers any mod adding an ordinary `CompGlower` and does *not* cover light arriving by
some other route: a mod passing sunlight through a window, anything drawing its own section layer,
anything lighting cells without registering a glower. Under total suppression every one of those goes
**black** rather than merely unimproved, and each has to be found and special-cased one at a time,
forever, as mods change. Under a floor they are all simply dim, and the list of things §27 has to
know about stops being load-bearing. That is worth more than the beam contrast it costs, and it is
the same reason the epic wanted the suppressing half droppable in the first place.

Off remains §27 as originally designed, for anyone who wants shadows that reach full dark and accepts
that a room lit only by light bending around a corner loses all of it.

### Phase 3: the subtractive mask (`vector_light_mask`)

Phases 1 and 2 drew a second lighting model over vanilla's; phase 2b tried to compose the two as a
max and measured a no-op. Phase 3 gives up on composing two models and **edits vanilla's**, which is
the only operator left once you notice that *§27's contribution is subtractive*: a shadow is light
taken away, and nothing that only ever adds can express one.

Per emitter, over the cells our polygon says that emitter cannot reach:

```
newGlow(c) = totalGlow(c) − Σ over our emitters of  own(e, c) · (1 − lit(e, c))
```

`own(e, c)` is vanilla's own per-emitter glow, read out of `GlowGrid`'s private per-light arrays
(`GlowGridPerLight`) — `ComputeGlowGridsJob` fills them with `falloff(geodesic distance)`, which is
exactly the light that bent around a corner. `lit(e, c)` is the share of the cell the visibility
polygon covers (`VectorLightMath.LitFraction`).

**What the shape buys.**

- **The level stops needing calibration.** A cell the polygon can see subtracts nothing, so it reads
  at exactly vanilla's own brightness. `DefaultStrength` existed to calibrate an additive pass
  against vanilla's multiply and never quite landed; there is nothing left to calibrate.
- **Daylight is free.** `DaylightScale` existed because the additive pass sat *above* the sky's
  multiply, so an unattenuated torch outglowed noon. This edits the value the multiply consumes.
- **Nothing we did not model is ever touched**, because we subtract a *named* emitter's own
  contribution and nothing else. A mod passing sunlight through a window, drawing its own section
  layer, or lighting cells without registering a glower is untouched *by construction* rather than
  by a floor. That is the compatibility problem `vector_light_blend` exists to manage, dissolved.

**Measured**, same scene and frame as the arms above:

| arm | lit room L\* | shadow off the beam L\* | doorway beam L\* | beam contrast | masked ΔE vs vanilla | frame touched |
|---|---|---|---|---|---|---|
| vanilla | 9.61 | 12.58 | 15.54 | 1.17 | — | — |
| crossfade @0.5 (shipped) | 9.72 | 10.73 | 15.76 | **1.38** | 0.95 | 6.27% |
| **mask (phase 3)** | 9.51 | **9.07** | 13.34 | **1.37** | 1.35 | **2.80%** |
| full §27, soft edges | 9.84 | 8.87 | 15.94 | **1.66** | 1.51 | 6.60% |

**It ties the crossfade on contrast and gets there from the other side.** Beam contrast 1.37 against
the crossfade's 1.38 — but the crossfade reaches it by *lifting the beam* above vanilla (15.76 vs
15.54) while the mask reaches it by *dropping the surroundings* below it (beam 13.34, shadow 9.73).
It gets §27's shadow depth almost exactly (9.07 against full §27's 8.87) while touching less than
half as much of the frame.

**What it cannot do is make a beam.** It only subtracts, so the light through a doorway can never be
brighter than vanilla put there — and the cells immediately past a one-cell gap are only *partly*
visible from the emitter, so they lose their unseen share and the beam comes out dimmer than
vanilla's. That is physically right and dramatically weaker. Phase 2b was the mirror of this: the max
kept vanilla's brightness and lost every shadow; the mask keeps every shadow and loses the beam. The
crossfade is the only one of the three that has both, at half strength each, which is a better
argument for the shipped default than the one originally written for it.

**The resolution objection, re-tested rather than inherited.** DESIGN.md rejected cell resolution for
§27 as "the resolution §27 exists to escape". At 4× zoom on a shadow edge
(`Tests/Screenshots/vector_light_penumbra__mask_edge_zoom.png`) there is **no staircase** — the edge
is a smooth ramp, because `LitFraction` reports a cell's covered *share* and the overlay's own
bilinear interpolation spreads it. It is a visibly *broader* edge than the polygon arms draw, which
is the good failure mode and the same direction phase 2 chose deliberately with a half-cell penumbra.

**Known approximation.** The subtraction happens in the overlay's post-projection byte space, while
`own` is read pre-projection. `ColorInt.ProjectToColor32` scales all channels by `255/max` once the
brightest exceeds 255, so where several bright emitters overlap we over-subtract by that factor —
always *darker*, never brighter. Single emitters are unaffected (a torch peaks at 172). The exact fix
is to rebuild the vertex colour the way `GenerateLightingOverlay` does, summing *all* of vanilla's
lights with our coverage applied only to the ones we modelled, and projecting once at the end;
`GlowGridPerLight` already exposes everything that needs.

**Performance: measured, then fixed, and the first measurement was not a comparison.** Dubs first
reported the mask as three times *cheaper* than feature-off, having provoked no regenerate at all —
the patch was absent from the table rather than fast. Re-measured through **Circinus**, which reports
call counts, one arm per process, our own postfix armed directly. That gave 239 µs per section
against the crossfade's 20.

That 11.8× was an artefact. The mask built its visibility polygons **lazily on first use, inside the
section bake**; the crossfade builds the same polygons in the draw path, so its bake row never
contained them. Found by arming the sub-methods: `Apply` read 49.2 ms while everything it calls
summed to 6.0 — `BuildCellShadow` 1.17, `ApplyToCorners` 3.41, `ApplyToCentres` 1.21, the reader 0.21
— and the missing 43 ms was `EnsurePolygon` under `CollectReaching`. **Three speculative
optimisations before that measurement moved the number by nothing at all.**

Four changes, in the order they were made and with what each was worth:

| change | mask ÷ crossfade |
|---|---|
| as first written | **11.8–15.3×** |
| geometry build hoisted out of the bake | 2.37× |
| unshadowed vertices skipped in both vertex passes | 1.76× |
| unshadowed sections never touch the mesh at all | **1.51×** |

Final, three interleaved repeats per arm (interleaving matters: scattered runs on this box swing 2–6×
on identical code, interleaved ones hold to ±5%):

| arm | median total | **µs per section** | worst frame | ÷ crossfade |
|---|---|---|---|---|
| crossfade @0.5 (shipped) | 4.64 ms | **20.7** | 2.35 ms | — |
| mask | 6.98 ms | **31.2** | 3.63 ms | **1.51×** |
| mask + beam | 5.67 ms | **25.3** | 3.27 ms | **1.22×** |

**It did not get below the crossfade, and on this scene it probably cannot.** The crossfade writes
every vertex of every section and stops; the mask writes the same vertices *and* works out what to
write. The two early-outs only pay where a section has no shadow, and `vector_light_perf.json` is a
deliberately hostile case for that — 23 emitters in walled rooms with every one on screen. An
ordinary colony, mostly unlit and unshadowed, is where the mask's skips actually fire, and the
ordering there is untested.

Every optimisation was verified behaviour-preserving on the live A/B rather than assumed: mask
9.51 / 9.07 / 13.35 and combo 10.56 / 9.07 / 16.39 are unchanged from before any of this work.

**One trap is worth carrying forward.** Hoisting the geometry build out of the bake meant a section
could bake while a polygon was still dirty; it skipped that emitter, nothing ever asked it to bake
again, and the mask rendered **pixel-identical to vanilla with every probe healthy**. Whoever builds
the polygons must re-dirty the map afterwards. `EnsurePolygons` therefore reports whether it built
anything, and both callers act on that.

**Lamps and SUN shadows (`Tests/Scenarios/vector_light_sun_shadow.json`).** Whether a lamp lifts a
sun shadow is decided entirely by draw order, and the answer differs per arm:

| altitude | layer |
|---|---|
| 18 | `Shadows` — building sun shadows, baked into a section mesh |
| 28 | `Pawn` |
| 37 | `LightingOverlay` — what the mask edits |
| 38 | `VisEffects` — where the beam draws |

The beam sits above the shadow layer, so it lands on top of a sun shadow and lifts it. The mask
cannot: it edits artificial light, and a sun shadow is not artificial light. Measured at dawn, a
seven-cell wall throwing a long shadow with a torch inside it, averaged over a **fixed 9,449-pixel
shadow set derived from the vanilla frame** so every arm is measured on the same pixels:

| arm | shadow L\* | at the lamp | vs vanilla |
|---|---|---|---|
| vanilla | 26.69 | 27.19 | — |
| crossfade @0.5 | 26.15 | 26.67 | **−0.54** |
| mask alone | 26.67 | 27.19 | **−0.02** |
| **mask + beam** | **29.31** | **29.94** | **+2.62** |

So the combination gets this for free and neither of the others gets it at all — the mask does
literally nothing (−0.02), and the crossfade makes the shadow slightly *darker*, because it halves
vanilla's artificial light everywhere including inside the shadow while its own pass is too diffuse
to make that back.

**Pawns need nothing either, and for the same reason.** They draw at 28, *below* the lighting
overlay, so the mask's per-cell edit lights them exactly as it lights the floor, and the beam at 38
lifts a pawn standing in it. This is a property the additive-only arms never had: full §27 draws at
38, **above** pawns, so its light lands on top of a pawn rather than lighting it.

**The hour is measured, not chosen.** A sun shadow needs the sun up and the beam needs the sky down —
`DaylightScale` is `1 − skyGlow` — so the two want opposite things. A quarter-hour survey put the
usable window much wider than feared: at hour 4.5 the sun is 9.4° up and the beam still carries 71%
of its strength. `limb_sun_elevation` is pinned at 9.40 next to the effect so a clock change fails
loudly rather than quietly emptying the frames.

**What this does not do** is make the shadow respond to *which* lamp can see it — the beam is simply
additive light landing on top. A sun shadow that fades only where a lamp genuinely reaches needs the
shadow's own shader to read a per-vertex light channel, because `Custom/Sun shadow`'s vertex alpha is
the **extrusion length**, not darkness: writing a smaller alpha makes the shadow shorter, not
lighter. That is a real feature and it needs #151's AssetBundle pipeline, and the mask is the only
arm that could supply the per-cell occluded light figure it would consume.

**What ships.** `vectorLights` is a settings toggle and still defaults **off** — §27 is the most
opinionated thing in the mod, and light that vanilla delivered around a corner no longer arriving is
a large enough taste call to be opt-in. `vectorLightPawnShadows` has its own switch beneath it, on by
default, because it is the one part of §27 that draws a new OBJECT rather than recolouring an
existing one — the same reasoning that gives §25's visible clouds their own switch.

**The composition is deliberately not exposed.** Mask and beam are what §27 is designed around and
both default on; the crossfade survives only as the fallback the code picks for itself when the
per-emitter glow arrays cannot be read. A player has no way to judge that choice and no reason to be
asked about it, and the flags remain switchable from the harness for measurement.

### §27e: open doors (`vector_light_open_doors`, `Tests/Scenarios/vector_light_open_door.json`)

**Problem.** A pawn stands in an open doorway with a torch behind them and the doorway is black. §27
draws a beam through a *bare* gap in a wall — that is its headline result — but put an ordinary
wooden door in the same gap and the beam never comes back, however wide the door is standing.

**Why vanilla looks that way, which is the whole design constraint.** RimWorld's glow grid never
learns that a door opened. `Verse.Building.SpawnSetup` writes `def.blockLight` into `GlowGrid`'s
`lightBlockers` bit array once, at spawn (`Building.cs:140`), `DeSpawn` clears it (`:242`), and
`Building_Door.DoorOpen` sets `openInt`, clears the reachability cache, raises
`Map.events.Notify_DoorOpened` — and touches the glow grid not at all. A vanilla door blocks light
open or shut. §27 inherited that for free by asking `blockLight` and nothing else, which is exactly
why glass doors already work (see `vector_light_glass_door`) and exactly why open ones do not.

So there is **no vanilla behaviour to mirror here**. Every other §27 rule is a restatement of one of
vanilla's; this is the first that is not, and that is what makes it a taste call rather than a fix.

**Approach.** `DoorOcclusionMath.Occludes(blocksLight, isDoor, doorOpen, openDoorsPassLight)` — four
booleans, tested exhaustively offline. `blockLight` is tested first so transparency is unaffected,
the flag second so off returns exactly the pre-feature expression, `isDoor` third because only a door
has an open state. `VectorLightBlockers` reads `Building_Door.Open` off the edifice it had already
fetched. Invalidation hangs off `MapEvents.Notify_DoorOpened`/`Closed` rather than
`Building_Door.DoorOpen`, which is `protected virtual` and gets overridden without `base` by exactly
the modded door classes this is for.

**This knowingly disagrees with gameplay light, and that is the point of contention.** With the flag
on we draw a beam vanilla does not deliver: `GroundGlowAt` still reads dark outside the door, plants
still do not grow, pawns still cannot see. It is permitted because §27's contract is that it changes
only what is *rendered* — but unlike §27's other divergence (a light's own cell treated as open:
static, one cell, always keeps a lit thing lit) this one is beam-sized and blinks as pawns walk
through. Issue #48 records the opposite sign of the same mistake. Hence: opt-in, off by default.

**What was measured, and why the second arm matters more than the third.** Four arms, one wooden
door, midnight, one torch:

| arm | `lit_area` | `shadow_frac` | `verts` | `glow_out` |
|---|---|---|---|---|
| 1 door shut | 455.4963 | 0.300943 | 440 | 0.095115 |
| 2 door open, flag **off** | 455.4963 | 0.300943 | 440 | 0.095115 |
| 3 door open, **drawn only** | 468.8965 | 0.280377 | 452 | 0.095115 |
| 4 door open, **glow grid driven** | 468.8965 | 0.280377 | 452 | **0.500000** |

Arm 2 is the control and it is **pixel-identical** to arm 1 — zero pixels differ — which is the live
proof that vanilla really is blind to open state and that our off path reproduces it exactly. Arm 3's
polygon lands on 468.896484 / 0.280377477 / 452, which is `vector_light_glass_door`'s **bare
doorway** to the last decimal: an open door now measures as the hole it visibly is. Arm 3 leaves
`glow_out` untouched, which is the contract holding; arm 4 moves it 0.095 → 0.500, which is the
contract deliberately broken.

Visually, against the shut door: the drawn-only arm touches **0.48%** of frame at masked median
**ΔE 1.74** (p90 4.87), the glow-grid arm **1.63%** at **ΔE 1.95** (p90 7.63). The footprints are the
interesting half rather than the medians — our polygon is a *beam* and vanilla's flood is a *wash*,
so the coherent option is three times the area for a fifth more contrast. Whether the beam or the
wash is what a doorway should look like is precisely the judgement the two flags exist to let
someone make by looking, which is why the rejected option is shipped alongside rather than described.

**`vector_light_door_glow_blocker` is the comparison arm, not a feature.** It moves vanilla's own
blocker bit on open/close, so gameplay light agrees. Known rough edge, recorded rather than papered
over: a door open at save time comes back with `openInt` true and no notification, so the grid
disagrees until the door is next used. `lightBlockers` is a bit array rather than a counter, so
nothing accumulates and it self-heals — acceptable for a flag that exists to be measured against.

#### Phase 2: tracking the slide (`vector_light_door_aperture`)

**Problem with phase 1.** `Building_Door.Open` flips true on the first tick of the swing, while the
leaves take tens of ticks to finish sliding. So phase 1 put a **full-width beam over a door the
player can still see closed** — the aperture and the artwork disagreeing for the whole animation,
which is the most conspicuous moment there is to disagree.

**Approach.** `DoorApertureMath` places the two leaves along the wall axis from `OpenPct`: each holds
half the cell when shut and recedes to its own side as the door opens, leaving a centred gap of
exactly `openPct` cells. `VectorLightBlockers` drops the door cell from the bool grid and hands those
two leaves to `Build` as ordinary segments.

**No new concept was needed for that**, which is the pleasing part. `SilhouetteSegments` can only
carry whole cells, but `Build` takes an arbitrary `Segment[]` and fires a corner ray at every
endpoint it is handed — it has no idea the other segments came from a grid. So sub-cell occlusion
rides alongside the silhouette, and because the corner rays land on the leaf edges, **the penumbra
tracks the leaves for free**.

**It models an illusion, knowingly.** Vanilla draws two movers, each a full 1×1 quad slid ±0.45·`OpenPct`
(`Building_Door.DrawAt` → `DrawMovers`). Two 1-wide quads sliding 0.45 cannot geometrically clear a
1-wide cell, so the visible opening comes from the door *artwork* inside those quads, not from the
quads' extents — there is no exact occluder outline to copy, only an apparent one. Modelling the
apparent thing is the appropriate kind of wrong for a feature whose whole claim is that the beam
looks like it tracks the door. Both ends are exact regardless: shut reproduces the closed-door
occluder, fully open reproduces a bare doorway, and both are pinned against measurements taken
before this existed.

**The cost, and the knob that bounds it.** `OpenPct` changes every tick, and every distinct value is a
fresh bake for the lights near that door — where §27's cost model assumed geometry changes when a
player *builds* something. `DoorApertureMath.Quantise` snaps it to eight steps, so a swing costs a
fixed number of bakes however slow the door or the game speed. Measured live:

| | rebakes per swing |
|---|---|
| tracking every tick (a 45-tick wooden door) | 45 |
| **quantised to 8 steps (shipped)** | **9** |
| phase 1, no tracking | 1 |

Nine, pinned at tolerance 0, and `door_aperture_watched` returns to **0** after the swing — the
watch set drains, so the sweep does not grow with the size of the base. The offline test states the
bound as a property rather than an example: however many distinct values `OpenPct` takes, the
quantised sequence takes at most `steps + 1` of them.

**Filmed, because a still cannot show this.** Two sweeps over the same wooden door's swing, differing
only in the flag. Integrated beam brightness in the yard outside the door, per captured frame:

```
aperture on    31.2  35.4  52.6  68.5  68.6 ... 79.0 ... 88.7      a ramp
aperture off   80.9  83.9  85.0  85.4  ...                        already there by frame 1
```

Phase 1 arrives at ~80 on the first captured frame — full width over a door still visibly shut.
Phase 2 starts at 31.2 and climbs to 88.7 across the swing.

**The instrument had to change to film it at all, and the first cut was a false pass.** TickLapse's
`AdvanceTicks` is a *jump*: it moves `TicksGame` without simulating, which is right for an effect that
reads the tick counter and wrong for one driven by a `Thing`'s own `Tick()`. A door's slide is the
second kind — `Building_Door.Tick` increments `ticksSinceOpen` and `OpenPct` is a ratio of it — so the
first film came back with the aperture pinned at 0 for all thirty frames, `door_aperture_bakes` at 0,
**and the scenario passing**. `SetTimeSpeed` (dev-only, in the probe bridge) unpauses the clock so the
door actually moves; the frames are hand-rolled `Wait`/`Screenshot` pairs because a jump cannot film
a simulated animation. Recorded here because a green run over a film of nothing is the exact failure
this repo's verification bar exists to catch, and it caught it only because a probe was pinned to a
value that had to *move*.

### Performance (`Tests/Scenarios/vector_light_perf.json`)

Epic #145 carried phase 5 with **nothing profiled at all** — phase 1's validation run was
`--no-profiler`, so §16's budget was entirely unverified. It is now measured, on 23 emitters (20
placed plus the three `minimal_colony.rws` already carries) each walled into its own room with
doorways between, camera zoomed out far enough that every one of them is on screen. That last part
is load-bearing: `VectorLightOverlay.Overlaps` culls against the view, so a tight zoom profiles the
culling and reports a fraction of the cost as though it were all of it. Three windows of 600 frames:

| window | `Patch_VectorLightDraw:Postfix` avg ms/frame | max ms/frame | share of a 60 fps frame |
|---|---|---|---|
| `gated` (`vector_lights` off) | **absent from the table** | — | 0% |
| `hard_edges` (phase 1) | 0.0389 | 0.370 | 0.23% |
| `soft_edges` (phase 2) | 0.0460 | 0.218 | 0.28% |

So **soft edges cost 0.007 ms/frame**, and the whole subsystem costs 0.046 — a quarter of the
≤0.20 ms/frame the epic asked for, with the switched-off path not appearing in the profile at all
against a ≤0.01 ms target.

Three things about that table are worth stating so it is not read for more than it says. The
per-window **totals** (0.42 / 1.05 / 0.81 ms) are *not* comparable and in particular do not show
soft edges being cheaper than hard ones: they are dominated by `Patch_IndoorSkyOcclusion`, whose
46–77 ms maxima are section regenerates provoked by the feature flip itself, and the windows ran at
52, 83 and 134 fps, so `PercentOfFrame` means something different in each. The per-patch row is the
comparable number. `SectionLayerDrawCounters:NoteDraw` costs 0.09–0.12 ms and is a **probe**, not
shipped. And the analyzer transplants timing calls into every patched method, so all of these are
ceilings.

### The flicker question, and what it turned out to be

Watching a run, the light reads as though it flickers slightly.
`Tests/Scenarios/vector_light_flicker.json` measures it rather than arguing about it: twenty ticks a
frame over twenty-four frames, which keeps `CurSkyGlow` still while everything tick-driven still
animates, swept three times — `vector_lights` off, phase 1, phase 2. Consecutive frames are then
diffed and the question asked is *where* they differ and *by how much*, not whether they differ.

| arm | peak channel diff outside the torch | px shifting >8 levels | of those, on the wall face |
|---|---|---|---|
| §27 off entirely | 60 | 65 | 59 |
| hard edges | 71 | 70 | 58 |
| soft edges | 75 | 71 | 56 |

**The flicker is overwhelmingly vanilla's.** It has two sources, and neither is this subsystem. The
bright one is the torch sprite: `TorchLamp` carries `CompProperties_FireOverlay`, which animates by
design — its glower is a flat `glowRadius` 10 and never moves. The subtler one is a sub-pixel shimmer
along every high-contrast edge in the scene, concentrated on the wall face the light stops against,
and it is **present at essentially full strength with the mod switched off** (59 px, against 58 and
56). §27 adds about 8% more high-amplitude pixels than vanilla alone; phase 2 adds one pixel over
phase 1, and on the wall face slightly *reduces* it.

That last part is not an accident but is smaller than it looks, and the reason is worth recording:
the shimmering edge is where the light is **clipped by a wall face**, not where it is cut by a
corner. Penumbra softens shadow boundaries, and a light terminating on the wall in front of it has no
corner and so gets no wedge. Softening that edge too would mean treating a wall face as an occluder
with a near limb, which is a different mechanism from this one.

**Caveat on where this was measured.** The camera is static in this scenario. A run watched live is
usually the *profiling* scenario, which sweeps at `superfast` with the clock racing — so
`VectorLightMath.DaylightScale` ramps every lamp down through dawn, over a frame rate the same run
measures at 52–134 fps with 46–77 ms section-rebuild spikes. Both of those read as the light pulsing,
and neither is the geometry.

**Not yet done, in the order it matters.** The caster set beyond walls, roof and window openings as
emitters (which would absorb issue #3's sun shafts into the same mechanism), and the lazy/pawn-range
work that is the declared second phase.

### Phase 4: pawns cast shadows away from the lamps that light them (`vector_light_pawn_shadows`)

Vanilla cannot do this, and it is not a gap in vanilla. Its pawn shadow leans on `_CastVect`, the
shader global `SkyManager` sets once a frame (§3), so *every* shadow on the map points the same way —
right for a sun, meaningless for a torch. So this is built as a **caster** problem rather than an
occlusion one: each pawn draws one quad per lamp in range, offset radially away from it, at
`pawns_in_view x lamps_in_range` rather than the ~1.9 ms per emitter that putting pawns into
`VectorLightBlockers` would cost every time one *stepped*. Phase 3's coverage grid supplies the one
thing the cheap version still has to get right — a pawn behind a wall must not throw a shadow from a
lamp that cannot see it — in a single array lookup.

Roofs and eaves are deliberately **not** skipped, against vanilla's own rule: `Graphic_Shadow` bails
on any roofed cell because sunlight does not get in, which is exactly backwards for a lamp.

#### Which rectangle the shadow leaves from (issue #159)

The first implementation anchored its quad on `pawn.DrawPos` and took a single scalar footprint. Both
were wrong, and the two faults were independent:

- **`ShadowData.offset` was not applied.** Vanilla anchors a pawn's shadow at `DrawPos + offset`
  (`Graphic_Shadow.DrawWorker`, and `Printer_Shadow.PrintShadow` for the printed path), and Human's
  `race.specialShadowData` declares `(0, 0, -0.3)` — at the feet. So a colonist's lamp shadow left
  their **torso** while their sun shadow left their **feet**, 0.3 cells apart.
- **The footprint was read from the field humanlikes do not have.** It asked
  `graphicData.shadowData`; `Races_Humanlike.xml` puts it in `race.specialShadowData`, which is where
  `PawnRenderer.DrawShadowInternal` reads it from. Every colonist in the game therefore fell through
  to a hardcoded `0.6` against a real `BaseX` of `0.3` — twice as wide as the sun shadow beside it.
  **Animals were unaffected**, declaring theirs inside `graphicData`, which is exactly why this
  survived being looked at.

The fix reads the same data vanilla reads (`race.specialShadowData ?? graphicData.shadowData`) and
spends it through one pure function, `VectorLightMath.FootprintExtent` — the **support function** of
the footprint rectangle. Passed the shadow's bearing it gives the distance from the anchor to the
silhouette's trailing edge, which is where the quad starts; passed the perpendicular it gives the
silhouette's half-width. That is one call answering both, and it is what makes the shadow
direction-dependent the way vanilla's is: a human presents 0.15 half-cells to a lamp due east and
0.20 to one due south. The push-out goes into the *transform* rather than the baked mesh, so the mesh
cache stays keyed on two numbers.

Two consequences worth stating because they are not obvious from the diff:

- **Starting at the trailing edge is what makes `PawnShadowLength` mean the same thing it means for a
  sun shadow** — length *beyond* the caster. Vanilla's shadow is the footprint quad *plus* a skirt
  extruded from the edge facing away from the light (§3), so measuring from the centre was short by
  half a footprint and overlapped the pawn's own blob.
- **The half-width bucket is a thirty-second of a cell where the length bucket is a quarter.** These
  widths are sub-cell, so quarter-cell buckets round every one of them to the same 0.25 and throw
  away the direction-dependence this exists to express. A length of 3.1 against 3.25 cells is
  invisible; a width of 0.15 against 0.25 is most of the shadow.

`MinPawnShadowHalfWidth` (0.125) is a floor against a def declaring a hairline `volume.x`, not a look
value, and it sits deliberately below every vanilla pawn — its predecessor was 0.175 and clipped a
human's whole 0.15–0.20 range flat, which is the same mistake in a different place.

**Verified where the bug lives.** `Tests/Scenarios/vector_light_pawn_shadows.json` cannot see any of
this: it is roofed on purpose, so `Graphic_Shadow` draws no sun shadow at all and there is nothing
for the lamp shadow to disagree with. `vector_light_pawn_shadow_anchor.json` is roofless with the
torch two cells north and a 23.9-degree morning sun to the east, putting the two shadows about ninety
degrees apart on one pawn. That elevation is measured rather than computed — `dayPercent = hour/24`
predicted 10.9 at the same clock reading, so the sun clock is not a bare fraction of the day and the
predicted pin would have failed a correct build. `vector_light_pawn_anchor_z` and `vector_light_pawn_width` pin the
rectangle itself — **both**, because they failed independently and either alone passes on half a fix.

#### Which pawns cast at all (issue #159, second half)

§27 renders a shadow vanilla does not in exactly one place — under a roof, because `Graphic_Shadow`
bails on roofed cells and "sunlight does not get in" says nothing about a torch. That divergence is
deliberate and is the point of the feature. The four *other* suppressions vanilla applies have
nothing to do with sunlight, and skipping them was not a decision, it was simply not asking:

- **Not standing.** `PawnRenderer.RenderPawnAt` only calls `DrawShadowInternal` when
  `results.posture == PawnPosture.Standing`. A pawn bleeding out on the floor is not a 1.2-cell-tall
  caster, and drawing them as one puts a full-height shadow beside a visibly flat body.
- **Psychically invisible.** `pawn.IsPsychologicallyInvisible()` is what sets
  `PawnRenderFlags.Invisible`, the other half of that same test. **This is the clause with actual
  gameplay consequence**: a shadow is the one thing that gives an invisible pawn away, and §27 is
  render-only by charter, so handing the player a tell vanilla does not is exactly what that scope
  boundary forbids.
- **Swimming.** `DrawShadowInternal` returns before any shadow for `Swimming ||
  DrawNonHumanlikeSwimmingGraphic` — the pawn is drawn part-submerged and a full blob beside them
  reads as floating.
- **Flying.** Vanilla does not suppress this one, it *substitutes*: a soft circle at
  `AltitudeLayer.Filth`, offset by the flight arc. §27 has no equivalent, and inventing one is a
  different feature, so it draws nothing rather than stamping a ground-caster's shadow under a pawn
  in the air.

`VectorLightMath.PawnCastsShadow` is the policy as one expression; `VectorLightPawnShadows
.CastsShadow` is the four live reads that feed it, each the read vanilla itself uses rather than
something that merely correlates (`GetPosture()`, not `Downed`).

**What a live capture can and cannot carry.** `vector_light_pawn_shadow_states.json` puts three
colonists in one torch's reach with the sun up at 5.29°, which makes it a test of *agreement* rather
than of absence: an upright colonist carries a sun shadow and a lamp shadow, and a downed one carries
neither. Before the gate, the anaesthetised pawn threw a standing-height lamp shadow while vanilla
gave them no sun shadow at all — the two subsystems visibly disagreeing about the same pawn in the
same frame. Median CIELAB ΔE over the shadow that vanished is **5.78** (p90 6.10), and it goes from
532 px to 0.

The other three clauses are **offline only, and the invisibility one is worth saying why.** Applying
`PsychicInvisibility` does not make a pawn invisible: `HediffComp_Invisibility` only flips over when
something calls `BecomeInvisible()`, which is the psycast's job, and no number of ticks substitutes —
`CompPostTick`'s own write to `lastBecameInvisibleTick` sits inside a branch that already requires
the pawn to be invisible. The scenario keeps a pawn carrying that hediff anyway, as a **control**
that must retain both shadows, which is what catches an implementation suppressing on the mere
presence of a hediff. Swimming needs water plus a live job and flying needs a `PawnFlyer`, neither of
which a paused scenario can hold still.

**Measured, against the pre-fix build of the same branch.** The shadow strip, isolated within each
run by differencing that run's own sun-only frame (which holds the pawn fixed — `SpawnPawn` generates
a fresh colonist per run, so a naive frame diff measures the sprite, not the shadow):

| | before | after | predicted |
|---|---|---|---|
| strip width | 32 px | 16 px | 0.6 → 0.3 cells |
| leading edge | y 543 | y 567 | 0.5 cells south (0.3 offset + 0.2 trailing edge) |
| strip length | 105 px | 108 px | unchanged |

At the 52.5 px/cell this scene renders at, that is 0.61 → 0.30 cells wide and 0.46 cells south —
both within a pixel of what the geometry says. Where the two arms' shadows disagree, median CIELAB
ΔE is **8.55** (p90 9.04, max 9.64); over the union of the two strips the median is 0.00, because
more than half of it is overlap where both arms draw the same shadow, and the whole-frame median is
0.00 for the usual reason a bounded object gives one.

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

## Interop: Realistic Planets 2 (`Source/RealisticPlanetsCompat.cs`)

**Problem — the same disagreement Planetsmith had, arrived at from the opposite direction.** Realistic
Planets 2 (`koth.RealisticPlanets2`) is a worldgen and climate overhaul: terrain, hydrology, a layered
climate model, biome placement, and a bundled fork of Map Mode Framework. One of the parameters the
player picks when generating a world is an axial tilt — five steps, which `AxialTiltCurves.GetTiltDegrees`
maps to 0 / 11.25 / 22.5 / 33.75 / 45° — and that tilt shapes the seasonal temperature amplitude, the
biome layout, and the size of the day/night temperature swing at every tile.

The direction it arrived from matters, because until recently there was nothing here to integrate
with. Its first Workshop release shipped a `Planets.PlanetaryLighting` namespace: its own sky
pipeline (`SkyPipeline`), its own geometric eclipses (`MoonShadowEclipseSystem`), a dynamically
regenerated per-building shadow layer, a lux registry, a moon on the world map — driven by patches on
`GenCelestial.CelestialSunGlow`, `SkyManager.CurrentSkyTarget`, `GenCelestial.CurShadowStrength`,
`GlowGrid` and `Printer_Shadow`. Two mods rendering one sky is the conflict class this mod exists to
avoid (see the Tilt the Planet! entry in `About.xml`'s `incompatibleWith`), and the correct answer
then was to stay out of the way.

The current release deletes that subsystem outright — 41 types gone, and the assembly no longer
references `SkyManager`, `GenCelestial`, `GlowGrid`, `SkyColorSet`, `Printer_Shadow` or `SectionLayer`
anywhere. Its remaining Harmony targets are worldgen (`WorldGenStep_*`, `WorldGenerator`,
`WorldGrid.RegisterPlanetLayer`, `FeatureWorker_MountainRange`), world-map UI (`PageUtility`,
`WindowStack`, `WITab_Planet`), `SavedGameLoaderNow`, and four `GenTemperature` /
`OverallTemperatureUtility` climate methods. **Zero overlap with ours.** What the deletion leaves
behind is a planet that was built for a tilt and that nobody lights on it: exactly Planetsmith's hole,
in a mod that had previously been filling it itself.

**Approach — take their obliquity AND their phase.** This is where the interop parts company with the
Planetsmith one, and the reason is that RP2 still runs a seasonal model after generation where
Planetsmith does not. `Planets.WorldGen.SolarGeometry.GetSunAltitudeAzimuth` computes a sun altitude
every time `GenTemperature.OffsetFromSunCycle` is asked for a tile's diurnal swing, off a declination
of

    declination = tilt × sin(2π × yearPhase)          // theirs
    declination = tilt × −cos(2π × dayOfYear / 60)    // ours, and vanilla's SunPositionUnmodified

and `−cos(x + π/2) == sin(x)`, so those are one curve read a quarter of a year apart: their solstice
lands on day 15 where vanilla's lands on day 30. `Formulas.RealisticPlanetsSolarDeclinationDegrees`
is our own curve evaluated at `dayOfYear + DaysPerYear / 4`, stated as a day offset rather than as a
second trigonometric expression so that `DeclinationSign` stays the only place in the mod where a
seasonal phase is written down.

**Why the phase comes too, when Planetsmith's tilt deliberately came alone.** The Planetsmith section
above turns on the claim that their tilt is a scalar with no phase behind it — no day-of-year term
anywhere in their assembly — so scaling our curve by their obliquity is the whole correct answer.
That claim is simply false of RP2: they have a phase, they run it every tick, and it drives a
gameplay quantity. Taking the tilt and keeping our own phase would light a planet whose sky peaked a
fortnight away from the daily temperature swing its own weather model is simulating. RP2 therefore
sits on RAT's side of the line drawn in `Formulas.SolarDeclinationDegrees`' comment — a whole
declination, not a scale — and is served by its own function rather than by the obliquity overload.

**The cost of that, stated because it is real and a player will see it.** RP2 does *not* patch
`GenTemperature.OffsetFromSeasonCycle`, so vanilla still owns the SEASONAL temperature cycle and
still runs it on `−cos`: coldest around day 0, warmest around day 30, growing season to match.
Following RP2's phase therefore puts our longest day about 15 days before RimWorld's warmest one. On a
vanilla or Planetsmith world those two agree; on an RP2 world they cannot, because RP2's own two
halves already disagree — its diurnal model is a quarter-year out of step with the seasonal cycle it
sits inside, which is an upstream inconsistency rather than one this interop introduces. The choice
here is only which of their two halves to match, and matching the one that describes the planet keeps
a single mod answering for the planet's geometry, which is the same ruling the RAT section makes.
This was raised as a concern before implementation and taken deliberately; the settings screen and
`About.xml` both say it in prose, because a sun peaking early reads as our bug otherwise.

**Precedence: RAT → RP2 → Planetsmith → our constant.** The chain is `AxialTiltCompat`'s else-arm as
before. RP2 goes above Planetsmith because it supplies a phase as well as a scale and is still
simulating the running year, which is the same kind of claim RAT makes; it goes below RAT because RAT
owns the live planet's geometry including its moon. All three installed at once is not a mod list
anyone should have — two worldgen overhauls fight over biome placement long before they reach us —
but the chain answers it anyway rather than asking who is installed in one place.

**Which tilt, and how it is read.** `Planets.Core.Planets_GameComponent.axialTilt` is a **public
static field**, scribed in that component's `ExposeData`, so it is per-save rather than per-instance
and there is no component to go and find. That makes this file shorter than `PlanetsmithCompat` in one
respect — no `World.components` walk, no `WeakReference` cache — and longer in another: the field is an
enum of theirs, so the degrees have to come from their `AxialTiltCurves.GetTiltDegrees`. We call it
rather than mirroring the ladder, because five step values are a design decision of theirs and a
copy here would drift silently the first time they retune one. The table is built once at bind time,
one `Invoke` per enum value, so the per-frame path is a static field get and a dictionary lookup —
a `MethodInfo.Invoke` in `SolarPosition`'s geometry path would be the most expensive thing in it by an
order of magnitude. A dictionary rather than an array because nothing guarantees their enum stays
zero-based and contiguous.

Because the field is static it keeps the last save's value after the player returns to the main menu,
so the read is gated on `Current.Game != null`. Without that gate a menu backdrop would be lit on a
planet that is no longer loaded — a failure mode Planetsmith's version gets for free from its world
lookup.

**Conflict risk.** No hard assembly reference; every member is a string resolved at runtime, so a
player without RP2 loads a build that has never heard of it. There is no negotiated API — these are
internal names, a weaker contract, treated as one: every resolve is null-checked, a miss logs once
naming the consequence rather than the fault, and the read is wrapped because a throw on the
per-frame geometry path would be one error per frame forever. An enum step we have never seen (they
add a sixth) falls back and warns once rather than indexing out of a table. NaN is rejected twice
over, at the read and again in `Formulas.SanitizeObliquityDegrees`, for the reason the Planetsmith
section gives.

**No opt-out,** for the reason spelled out at length above: `realistic_planets_geometry` is a harness
flag so a scenario can reach both arms in one run, nothing in a shipped game writes it, and the
settings screen reports rather than asks. What it reports gains one line here that the other interops
do not need — when RP2 is the source in force, the tooltip says the calendar runs a quarter early, so
the early solstice is documented where a player will actually meet it.

**Testing.** The pure half is `FormulasRealisticPlanetsPhaseTests`: their `sin` formula reproduced
independently and checked against ours at quarter-day resolution across every tilt step, the
direction of the offset (a sign slip would still look like "a quarter of a year" in any summary
statistic), the two days where one model is flat and the other is at full swing, periodicity past the
end of the year, an upright planet staying seasonless, and the sanitizer's clamp and NaN fallback
reaching this path too. The live half is `Tests/Scenarios/realistic_planets_tilt.json`.

That scenario needs a world with a non-default tilt for the same reason Planetsmith's does — the tilt
is chosen in RP2's world-gen UI and frozen into the save, and `minimal_colony.rws` predates RP2, so it
loads at the scribe default of `Normal`, 22.5°, less than a degree from our own 23.44°.
`RealisticPlanetsTiltOverride` (dev-only, under `Source/Probes/`) bridges a
`realistic_planets_steep_tilt` flag that writes their `VeryHigh` step and restores the original on the
way out. It parses the step out of their enum by NAME rather than writing an ordinal, so a renamed
step fails loudly instead of selecting a different planet. The 90°-is-the-end-of-the-slider worry that
ruled out the maximum for Planetsmith does not apply: 45 is not the ceiling of anything of ours
(`Formulas.MaxObliquityDegrees` is 90), so a clamp pinning every tilt to its maximum would show up
here as 90 and fail.

The day the scenario samples is day 15, and that choice is the whole of its sensitivity: our
declination there is exactly zero while theirs is the full tilt, so the two arms of the A/B are "no
season at all" against "a 45° planet at solstice" — the largest signal this interop can produce, and
one no tolerance can absorb. `AxialTiltDeclinationProbe`'s own comment already named day 15 as the
discriminating day for RAT, whose phase convention RP2 shares.

**How much reaches the screen, measured.** The handover is total and exact — every declination and
elevation pin landed on its analytic value to within 1e-5° on the first run, with no re-derivation:
22.5° at their default step, 45° with the override, −45° at their midwinter, 0° with the feature off
(day 15), and 0° again at day 30 with the feature on, which is the quarter-year offset showing up as
a crossing rather than as a number. Sun elevation at 55°N followed: 57.50 / 80.00 / 35.00, each the
analytic `90 − |lat − decl|`.

On screen, at noon on day 15, latitude 55°, on the shipped Cinematic preset (median per-pixel CIELAB
ΔE, CIE76):

| comparison | median ΔE | what it is |
|---|---|---|
| feature off → on, their default tilt | **2.25** (mean 2.90) | what a player with an untouched RP2 world sees |
| feature off → on, their `VeryHigh` tilt | **2.25** (mean 2.27) | the override arm |
| their default tilt → their `VeryHigh` tilt | **0.00** (mean 0.81) | tilt magnitude alone, at noon |
| their midsummer → their midwinter | **30.26** | day 15 against day 45 |

**The row that surprised us is the third one, and it is the one to read first — and the reason it is
not what it looks like.** Going from a 22.5° planet to a 45° one moves noon from 57.5° to 80° of
elevation and moves the median pixel not at all. That reads as "the tilt does not apply", so it is
pinned rather than argued: `shadow_extrude_far_cells` goes **0.892 → 0.247** across exactly that
handover, which is cot(57.5°) × 1.4 and cot(80°) × 1.4 to five decimals. The tilt reaches the
renderer in full; a 3.6× change in shadow length simply does not move a median taken over a frame
that is mostly sunlit ground. The mean does move (0.81 against a median of exactly zero), and it
moves further under Cinematic than under Realistic (0.81 vs 0.36) precisely because Cinematic's 1.4×
shadow scale makes those shadow pixels bigger — which is itself the confirmation that the difference
lives in the shadows and nowhere else.

So everything *visible in colour* in the first two rows is the PHASE, not the tilt: at day 15 our own
curve is flat and theirs is at full swing, so the interop's whole contribution to the sky here is
having moved the sun off the horizon at all, and above roughly 57° of elevation the sky has saturated
and stops responding. That is a good outcome for this design — the half that was argued hardest for is
the half doing the work — but it means a future A/B that varies only the tilt step, at noon, in
midsummer, will measure ΔE 0 and must be read with the shadow probe beside it.

The fourth row is the same phase seen from the other end, and is included because it is what a player
actually experiences: on a 45° planet at 55°N, RP2's midwinter puts the noon sun 10° below the
horizon (pinned: `sun_elevation` −10.00). `realistic_planets_steep_tilt_day45.png` is a night frame
taken at 12:00.

**The quarter-year offset is exact, and the proof for it moved during review.** `feature off, day 15`
and `steep tilt, day 30` are both declination zero at the same hour and latitude, reached from
opposite sides — one by our phase being flat, the other by theirs. Measured against the base this
branch was written on, the two frames came out **pixel-identical**, ΔE 0.00 including the mean, which
was a satisfying way to show that the offset is exactly a quarter of a year rather than approximately
one.

Rebasing onto §22 broke that, and correctly: the two frames sit on different days of the year, §22
gives each day its own cloud fraction, and the pair now measures **ΔE 1.08**. Nothing about the
geometry changed — `sun_elevation` reads 34.9999962 in both, to the digit — so what the rebase
removed was a coincidence the check was leaning on, not the property it was checking. The real proof
was always the probe: `axial_tilt_declination` reads 1.02e-06 at one and −5.37e-07 at the other, zero
to six decimal places on both sides, and both are pinned. Recorded here rather than quietly
re-baselined because a screenshot pair that stops being identical is exactly the shape of thing that
gets explained away.

Measured on Cinematic, the shipped default, with the persisted settings file cleared first — this box
had been carrying Realistic, and `run_test.sh` does not reset mod settings. That skew is worth
knowing about because it is silent: under Realistic every `shadow_extrude_far_cells` pin in
`planetsmith_tilt.json` fails by a constant 1.0/1.4, which reads as a shadow regression and is only
the preset's `shadowLengthScale` going live.
