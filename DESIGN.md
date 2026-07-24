# CelestialLighting — Design

## Problem

"Tilt Planet! – Realism Overhaul" (Workshop 3520836521, delisted) had lighting the user liked —
axial-tilt-driven shadow direction, dramatic seasonal twilight — bundled with unrelated
economy/material changes. No code from it exists anywhere accessible; this mod is built purely
from public Workshop screenshots/description text plus decompiling *vanilla* `Assembly-CSharp.dll`
to understand RimWorld's existing celestial/sky systems. Scope is visual/atmospheric only — no
pawn work-speed or move-speed penalties.

Phase 1 covers exactly three effects: shadow direction, twilight color, and (experimental) a
subtle per-position shadow-length tilt across a single map.

## 1. Shadow direction (`Patch_ShadowDirection`)

Vanilla's `GenCelestial.GetLightSourceInfo(Map, LightType.Shadow)` computes its result with zero
latitude dependence — the y-component is always `num2 - 2.5f * (num4*num4/100f)`, `num2` always
`-1.5f`/`-0.9f`, so vanilla shadows lean the same way on every tile, in every season. Real-world
shadows flip depending on hemisphere and lean with the sun's seasonal declination.

A Harmony Postfix on `GetLightSourceInfo`, active only for `LightType.Shadow`, blends the sign of
`__result.vector.y` toward a latitude/season-derived `lean` value in `[-1, 1]` (see
`LatitudeEffect.cs`): `hemisphereSign * declinationSign * strength`, where `strength` ramps from 0
at the equator to 1 by 60° latitude, and `declinationSign` is the same one-line sinusoidal
day-of-year term vanilla's own `GenCelestial.SunPositionUnmodified` already uses
(`-cos(dayOfYear/60 * 2π)`, reusing `GenDate.DaysPerYear`).

The blend is a sign-interpolation, not a lerp toward the literal negation:
```
targetSign = sign(lean)
y' = lerp(y, |y| * targetSign, |lean|)
```
An earlier version of this formula lerped `y` toward `-y` directly, which collapses `y` to exactly
`0` whenever `lean == 0` — not just at the equator but at *every equinox, at every latitude*,
flattening shadows everywhere twice a year. The sign-blend makes `lean == 0` a true no-op (vanilla
untouched) while still reaching a full flip continuously as `|lean|` approaches 1 — no
discontinuity at the equator or equinox, no equinox-flattening artifact.

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

## Conflict risk

Decompiled the user's local Dub's Skylights 1.6 copy (`Dubwise.DubsSkylights`) — its patches
(`Patch_GameGlowAt`, `Patch_NeedInterval`, `Patch_SectionLayer_LightingOverlay_Regenerate`,
`Patch_SetRoof`, `Patch_SpawningWipes`, `GardenPatches`) touch none of `GetLightSourceInfo`,
`CurSkyTarget`, or `SectionLayer_SunShadows.DrawLayer`. Dub's Skylights reads
`SkyManager.CurSkyGlow` (the map's overall glow value), which none of these three patches modify —
we only touch shadow-vector direction/length and `CurSkyTarget`'s *colors*, never `.glow` itself.
So existing light sources (`CompGlower`, `GlowGrid.GroundGlowAt`) are entirely unaffected by this
mod: there's no shared computation to skip or interact with in the first place.

`SectionLayer_SunShadows` is a leaf, internal, non-subclassed vanilla type with a single
`DrawLayer()` override — low risk of another mod patching the exact same method, but if one does,
Harmony will only run whichever prefix returns `true` from `__runOriginal` handling last (both
returning `false` means only one prefix's replacement drawing actually runs); no known mod in this
setup currently does.

## Clean-room provenance

Both formulas are original, reusing only one public-domain trig line already present in vanilla's
`GenCelestial.SunPositionUnmodified` (a standard sinusoidal day-of-year declination term, not a
substantial or copyrightable expression). No code, assets, or shaders from Sjaandi's mod were ever
available to reference. The shadow-tilt subsystem deliberately avoids writing or shipping a custom
shader — it only calls Unity's existing `MaterialPropertyBlock` API against RimWorld's own,
already-compiled `MatBases.SunShadow` material.

## Pure-function core (`Source/Formulas.cs`)

Every formula above — latitude/season context, the shadow-lean sign-blend, the twilight band/
factor curve, and the shadow-length position/scale math — lives in `Source/Formulas.cs`, a static
class with no `UnityEngine`/`Verse` dependency at all (only `System`). `LatitudeEffect.cs` and the
three patch files are thin adapters: they pull primitives off live `Map`/`Section`/`Find` state
and hand them to `Formulas`, which does the actual math and returns primitives/plain structs back.

`Tests/CelestialLighting.Tests/CelestialLighting.Tests.csproj` links `Source/Formulas.cs` directly
into the test project via `<Compile Include>` (not a copy), so `FormulasTests.cs` exercises the
exact code that ships, running standalone under `dotnet test` with no RimWorld/Unity assembly
present. This exists because a real formula bug (the equinox-flattening shadow-lean issue
documented above) was caught by a one-off manual review, not by any automated test — the API
compatibility tests below only check that vanilla members still exist, not that our own math is
correct. `FormulasTests.cs` covers each function's edge cases directly, including a regression test
that `ApplyShadowLean` is a true no-op at `lean == 0` for every `y`, not just `y == 0`.

## API compatibility tests

`Tests/CelestialLighting.Tests/ApiCompatibilityTests.cs` uses Mono.Cecil to verify every vanilla
type/method/field these patches depend on still exists, including asserting
`GenDate.DaysPerYear == 60` by value (not just existence) since `LatitudeEffect`'s `/60f` divisor
would silently desync seasons if that constant's value ever changed. Run `./test.sh` before
loading the game after any RimWorld update — it runs both `ApiCompatibilityTests` and
`FormulasTests` together.
