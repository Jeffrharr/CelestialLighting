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
grading the scene. Genuine vividness needs a `SkyOverlay` with spatial structure and movement, which
is [issue #42](https://github.com/Jeffrharr/CelestialLighting/issues/42); if that lands, these two
constants drop back toward vanilla's as the subtle base layer underneath it.

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
cells, pinned by a test — a gap there would mean a cell that neither casts nor occludes.

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
  at 61% of the sky, precisely the bug §7b exists to fix) and every cell of it would start casting a
  roofline shadow. There is no sky under a mountain in any case, which is the same exception vanilla
  itself makes in `SectionLayer_LightingOverlay`.
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
predicate. There are now three questions, and keeping them apart is the whole design:

| Question | Asked by | Cavern | Orbit | Open air |
|---|---|---|---|---|
| `HasSky` — can weather roll overhead? | §13 only | no | **no** | yes |
| `IsEnclosed` — is there a ceiling? | §2, §5, §8, §10, §10a, §11, §12 | **yes** | **no** | no |
| `DrawsShadows` — does this map draw shadows? | §1, §3, §4, §6a, §13, §15 | no | **yes** | yes |

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

**Conflict risk.** `MapSky` reads only vanilla `BiomeDef` fields (`disableSkyLighting`,
`disableShadows`, `inVacuum`, `baseWeatherCommonalities`) and takes no reference to any third-party
assembly, so there is nothing here for another mod to collide with — a mod that wants its map
treated as enclosed gets there by declaring biome data, exactly as Biomes! Caverns already does.
Not cached, deliberately, even at nine callers: every call is per-map-per-frame at worst, and a
BiomeDef-keyed cache would have to be invalidated against the harness's own `SetBiome` step, which
mutates `map.Biome` at runtime so scenarios can sweep biomes inside one run.

## Settings and presets

Two cross-cutting settings ideas that span the subsystems above:

- **Opinionated presets.** Ship a small number of named presets (e.g. "Realistic" vs
  "Cinematic/Pretty") that set the correlated knobs together — shadow length/strength (§1),
  desaturation strength (§9), weather dimming (§13), and the two minimum-brightness floors (outdoor
  §7, indoor §7b) — so most players pick one preset and never open a slider. Individual sliders
  remain for anyone who wants them.

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
2.1MB `PreviewBig.png` (kept locally, never committed), `.git/`, and a `.pdb` whose portable-PDB
metadata carries absolute `/home/deck/Developer/...` build paths. Roughly 600KB of mod inside tens
of MB of scaffolding.

**Approach.** `publish.sh` stages a whitelist into `dist/CelestialLighting/` and points both
uploads at that tree rather than at the repo. The whitelist is the entire mod — `About/About.xml`,
`About/Preview.png`, `About/PublishedFileId.txt`, `1.6/Assemblies/CelestialLighting.dll` and
`LICENSE` — because this mod adds no defs, textures or sounds, only patches. `LICENSE` is there for
MIT's one obligation: the notice has to accompany copies, and a subscriber's mod folder is their
copy — About.xml naming MIT is discoverability, not the notice. Steam gets it via a generated
`workshop.vdf` fed to `steamcmd +workshop_build_item`; GitHub gets the same tree zipped and
attached to a release by `gh release create`.

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

Three guards, all covering failures that are silent at upload time:

- A version directory in the repo that `About.xml` does not declare (or vice versa) aborts the run.
  Shipping assemblies under an undeclared version means RimWorld never loads them; declaring a
  version with no assemblies means the mod loads and does nothing.
- A directory RimWorld treats as loadable content (`Defs`, `Textures`, `Sounds`, `Patches`,
  `Languages`, `Sprites`, `AssetBundles`) that no whitelist entry draws from aborts the run. This
  is the whitelist's one real hazard — add content, forget the manifest, publish a mod missing it —
  and it stays invisible until users report it.
- `--dry-run` writes `dist/workshop.vdf` and prints both upload commands without running them.
  Both uploads are hard to walk back; this is the intended way to inspect what would ship.
