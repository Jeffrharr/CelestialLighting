# Rimworldmods

Use the CLAUDE.md at /home/deck/Developer/RimWorldMods/CLAUDE.md first

# CelestialLighting

A clean-room lighting mod for RimWorld 1.6, built to replace "Tilt Planet! – Realism
Overhaul" (Steam Workshop 3520836521, author Sjaandi) in this user's mod list.

## Why this exists

The original mod had lighting effects the user liked — cloud-dimmed skies,
moon-phase-driven night darkness, axial-tilt seasonal sun angle, and true
pitch-black unlit nights — but bundled them with unrelated economy/material changes
(construction costs, mining yields, structure HP) that tend to break things. The
original has since been **removed from Steam Workshop for violating content
guidelines** and was never open-source, so there is no code of theirs anywhere to
reference. This mod is implemented purely from public material (the Workshop
description text and a handful of its screenshots) plus decompiling *vanilla*
`Assembly-CSharp.dll` to understand RimWorld's existing celestial/sky systems —
never Sjaandi's code, which was never available in the first place.

**Scope is visual/atmospheric only.** No pawn work-speed or move-speed penalties
from darkness — the original coupled darkness to a stat penalty; we deliberately
don't.

## What we're building

Four subsystems:

1. **Moon phases** — `GameComponent_MoonPhase`, game-wide (one moon shared across
   all maps/tiles), tracks a configurable-length lunar cycle and exposes a phase
   fraction (0 = new, 1 = full) plus a labeled enum (New, Waxing Crescent, First
   Quarter, Waxing Gibbous, Full, Waning Gibbous, Last Quarter, Waning Crescent).

2. **Cloud cover** — `MapComponent_CloudCover`, per-map, a smoothly varying 0–1
   opacity value from layered value noise (sampled hourly, not per-frame),
   independent of `WeatherDef` selection. Categorical label (Clear/Patchy/
   Overcast/Black) for the HUD.

3. **World-map terminator overlay** — a new `WorldDrawLayer` subclass shading the
   globe mesh by `Vector3.Dot(vertexNormal, GenCelestial.CurSunPositionInWorldSpace())`,
   producing a soft day/night gradient band across the world map. Purely cosmetic.

4. **Pitch-black nights** — a single Harmony postfix on `SkyManager.SkyManagerUpdate`
   that, when enabled (default **on**, user-toggleable off) and moon phase + cloud
   cover are both low/high enough, further darkens `MatBases.LightOverlay.color` /
   `MatBases.FogOfWar.color` and calls `map.skyManager.ForceSetCurSkyGlow(...)` with
   the same reduced value.

Plus a small HUD extension (two Harmony postfixes on `RimWorld.DateReadout`'s
`Height` property and `DateOnGUI(Rect)`) showing moon phase + cloud cover under the
vanilla date readout, and a settings screen for all the toggles/tunables.

## Key architectural findings (from decompiling vanilla 1.6)

- **Axial tilt / seasonal sun angle is already vanilla.** `RimWorld.GenCelestial`
  has `SunOffsetFractionFromLatitudeCurve` and `SunPeekAroundDegreesFactorCurve`,
  which already vary sun position and day length by latitude and season, including
  near-total darkness/light above ~70–75° latitude. We do **not** reimplement solar
  math — subsystem 3 above only visualizes what `GenCelestial` already computes.

- **`Verse.SkyManager.CurSkyGlow`** (and its public `ForceSetCurSkyGlow(float)`
  setter) is the canonical "how bright is it right now" value other mods already
  read. Confirmed by decompiling the user's local copy of **Dub's Skylights**
  (Workshop 833899765 — `1.6/Assemblies/Dubs Skylight.dll`): its
  `Patch_GameGlowAt` postfix on `GlowGrid.GroundGlowAt` reads exactly this
  property. Because our darkening writes through `ForceSetCurSkyGlow`, Dub's
  Skylights automatically sees correctly-darkened nights for skylit rooms — no
  separate compat patch needed for it.

- No vanilla day/night terminator visualization exists on the world map — subsystem
  3 is wholly new but low-risk (rendering-only, no gameplay math).

## Conventions to follow (see parent CLAUDE.md at `/home/deck/Developer/RimWorldMods/CLAUDE.md`)

- `Source/CelestialLighting.csproj`: `net481`, `Krafs.Rimworld.Ref 1.6.*`,
  `Lib.Harmony 2.3.*`, output to `../1.6/Assemblies` (match `PerformanceSearch`'s
  csproj).
- `About/About.xml` with `loadAfter` baseline (Ludeon core + DLCs, `brrainz.harmony`)
  plus `Dubwise.DubsSkylights` (soft interop note, not a patch-order conflict).
- `DESIGN.md` documenting the architectural decisions above in the house style
  (Problem / Approach per subsystem / Conflict risk / clean-room provenance note) —
  see `PerformanceSearch/DESIGN.md` for the structure to match.
- `Tests/CelestialLighting.Tests/ApiCompatibilityTests.cs` — Mono.Cecil tests
  (pattern: `PerformanceSearch/Tests/SearchFix.Tests/ApiCompatibilityTests.cs`)
  verifying every vanilla member this mod depends on still exists:
  `GenCelestial.CurSunPositionInWorldSpace`, `GenCelestial.CurCelestialSunGlow`,
  `SkyManager.CurSkyGlow`, `SkyManager.ForceSetCurSkyGlow(float)`,
  `SkyManager.SkyManagerUpdate`, `MatBases.LightOverlay`, `MatBases.FogOfWar`,
  `DateReadout.Height`, `DateReadout.DateOnGUI(Rect)`.
- `build.sh` / `test.sh` matching PerformanceSearch's scripts.
- Over-document code with comments explaining *why*, per the parent CLAUDE.md's
  rule for our own mods (as opposed to matching another author's style — there's no
  other author's code here to match).

Full implementation plan, including verification steps, lives at
`/home/deck/.claude/plans/purring-coalescing-koala.md` from the planning session
that produced this design.
