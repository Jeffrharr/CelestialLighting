# CelestialLighting

Read `/home/deck/Developer/RimWorldMods/CLAUDE.md` first — it carries the shared code style, the
worktree rule, the decompiler invocation, and the live-harness / ΔE material that applies to every
mod here. This file is only what is specific to *this* repo.

**`DESIGN.md` is the authority on what the mod does and why.** It is ~5,000 lines organised as
numbered subsystems (§1–§21 plus lettered sub-subsystems), each with its own Problem / Approach /
what-was-rejected / verification notes. Do not restate its physics here — this file exists to
orient you and to record repo conventions and traps, and duplicating design detail into it is
exactly what let it go stale for months (issue #91).

**Where `DESIGN.md` and the code disagree, the code wins.** `DESIGN.md` is maintained but large, and
sections written early describe an arrangement later sections changed. Verify against `Source/`
before relying on a claim, and fix the doc when you find drift rather than working around it.

## Why this exists

A clean-room replacement for "Tilt the Planet! – Realism Overhaul" (Workshop 3520836521, Sjaandi,
since delisted). That mod had lighting the user liked bundled with unrelated economy/material
changes. It was never open-source and no code of it is accessible anywhere; everything here is built
from its public Workshop description/screenshots plus decompiling *vanilla* `Assembly-CSharp.dll`.
Keep it that way — the clean-room provenance note at the end of `DESIGN.md` is a claim we have to be
able to keep making.

**Scope is visual/atmospheric.** No work-speed or move-speed penalties from darkness; the original
coupled darkness to a stat penalty and we deliberately don't. The mod is shipped and published
(`joof.celestiallighting`), so regressions reach real subscribers.

Two things are *not* purely cosmetic and are opt-in for that reason: natural eclipses and realistic
day length. Everything on by default leaves gameplay light untouched, with one structural exception
worth knowing: a few patches write `SkyTarget.glow` (§7's night floor, §17's enclosed-map ambient,
§18d's limb refraction), and that *is* gameplay light — it is what Dub's Skylights reads. Colour and
overlay changes are not. Grep for `.glow =` in `Source/Patch_*.cs` to see the current set.

## What is actually here

Roughly twenty numbered subsystems covering solar geometry and shadows, night radiance
(starlight/airglow/moonlight), the moon, sky colour temperature, low-light desaturation, weather
dimming, eaves, eclipses, auroras, indoor sky occlusion, map-kind gates, vacuum maps, ozone
twilight, site altitude and aerosol, and the snow–cloud light cavity. See `DESIGN.md`'s section
headings for the real list.

Things the *old* version of this file claimed that were never built, and that have already cost one
implementation real time (issue #91, and see PR #99's write-up): there is **no**
`MapComponent_CloudCover`, no world-map terminator `WorldDrawLayer`, and no `DateReadout` HUD
extension. If you need a slow-cadence per-map cache, copy `SunClock.cs` — and prefer a static memo
over a new `MapComponent`, because `Map.ExposeComponents` scribes a permanent node per component and
deleting the type later logs two red errors per map (`MapComponent_SunShadowAxis.cs` is the tombstone
that documents this).

## Layout and conventions

| Path | What it is |
|---|---|
| `Source/*Math.cs`, `Formulas.cs` | Pure cores — no `UnityEngine`/`Verse` usings, primitives in and out |
| `Source/Patch_*.cs` | Harmony adapters: read live state, call a pure core, write the result back |
| `Source/SectionLayer_*.cs` | Our own map draw layers (§9 desaturation, §15b eave shade) |
| `Source/Probes/` | `IProbe` implementations for the live harness — excluded from the shipped DLL by `<Compile Remove>`, compiled instead into `TestMod/CelestialLighting.Probes.csproj` |
| `TestMod/` | The probe-bridge mod (`joof.celestiallighting.probes`), dev-only, never published |
| `Tests/CelestialLighting.Tests/` | NUnit + Mono.Cecil, net8.0, runs with no RimWorld present |
| `Tests/Scenarios/*.json` | Live harness scenarios (steps + probe pins + screenshots) |
| `Tests/Screenshots/` | Committed A/B captures referenced from PR bodies |
| `Tools/` | Offline utilities that link the shipped pure cores: `AuroraPreview` (renders §11a's field to PNG/GIF), `AuroraBench`, `WeatherAudit` (runs §13's classifier over every installed `WeatherDef`), and `ScenarioGen` (Python generators for the long scenario JSONs) |
| `publish.sh` | Stages a curated `dist/` tree for Steam + GitHub — never upload the repo directory itself |

`./build.sh`, `./TestMod/build.sh`, `./test.sh`. The shipped DLL lands in `1.6/Assemblies/`, which is
gitignored.

A `pre-commit` hook at `.githooks/pre-commit` runs `build.sh` and `test.sh` and blocks the commit if
either fails. It is not enabled by default (`core.hooksPath` is local git config, not versioned) —
run `git config core.hooksPath .githooks` once per clone/worktree to turn it on.

Conventions worth knowing before you write anything:

- **Pure core linked, not copied.** `Tests/CelestialLighting.Tests/CelestialLighting.Tests.csproj`
  pulls each pure file in
  with `<Compile Include ... Link=...>` so the tests compile the exact shipped file. Adding a new
  `*Math.cs` means adding a link entry — and the entries carry comments explaining each file's
  dependency order, so add yours in the same style rather than appending blindly.
- **The `inVacuum` gate.** Read `Source/Vacuum.cs` before adding a vacuum branch anywhere. The
  convention is fixed: the adapter calls `Vacuum.InVacuumForMap(map)` once, the pure function takes
  `bool inVacuum` as its **last, required, never-defaulted** parameter and early-returns at the top,
  and the offline test pins the vacuum value and its sea-level counterpart in the same sweep.
- **Map-kind gates.** `MapSky` / `MapSkyMath` answer "does this map have a sky / a visible sun /
  weather overhead" (§17). Anything sky-derived must ask, or it lights caverns.
- **Feature flags.** `CelestialLightingFeatures.cs` holds a static bool plus a string key per effect.
  The key is what scenario JSON's `SetFeature` step uses, so flag and key must stay together. A flag
  turned **off must reproduce the pre-feature behaviour exactly**, not "no effect at all" — that is
  what makes the harness A/B a real baseline instead of a picture of the mod being absent.
- **Settings are one preset bundle plus sliders** (`CelestialSettingsMath`, Cinematic default vs
  Realistic). A preset is never a separate code path; it writes the same persisted values.
- **Per-frame geometry goes through `GeometryMemo` / `FrameStamp`** — solar/lunar geometry used to be
  recomputed ~14× per map per frame (issue #12).
- Over-document with comments explaining *why*, per the parent CLAUDE.md's rule for our own mods.

The great majority of patch classes target just two vanilla members, `WeatherWorker.CurSkyTarget` and
`SkyManager.SkyManagerUpdate`, so ordering between our own patches is a live concern, not a
hypothetical one.

## The verification bar

Offline unit tests and the Mono.Cecil API tests are **necessary and insufficient**: they prove the
arithmetic and prove the vanilla members still exist, and a change can pass both while being
invisible on screen. The bar in this repo is a **live A/B run for every PR with a visual result**,
with the captures committed to `Tests/Screenshots/` and the measurement quoted in the PR body.

Measure **median per-pixel CIELAB ΔE (CIE76)** between the A and B frames — see the parent CLAUDE.md
for the thresholds, the reason the median rather than the mean, and how to invoke the harness. Two
things worth internalising beyond that:

- A change measuring under ΔE 1 is not shipped, however correct its maths. §20c (#99) was merged
  knowingly inert on exactly that basis, with the reason recorded rather than the number quietly
  dropped.
- Quote it against the siblings, so a new subsystem's strength is a comparison rather than an
  opinion. The measured set so far: §20c aerosol drift **0.36**, §19b ozone column **1.48**, §20 site
  altitude **1.88**, Realistic Planets 2 interop at their default tilt **2.25**, §21 snow cavity at
  overcast noon **6.06**, §20b pollution at 1.0 **6.79**.
- The cloud lanes are quoted as **p90, not median** — they draw bounded objects over part of a frame,
  which is exactly what a median hides (§25 at noon: median 0.00, p90 7.52). §25b's cloud varieties
  measure p90 **6.15** at noon and p90 **0.00–1.29** across the sunset, i.e. verified by probe and
  close to invisible in the frames; the section says so rather than quoting only the daylight number.

Scenario notes: `Tests/Scenarios/core_design_suite.txt` batches the scenarios that are safe to run in
one RimWorld load. Anything that leaves a `GameCondition` behind is deliberately excluded and must
run as its own process — the file lists them and says why.

## Traps

- **Section numbers collide.** Grep `DESIGN.md` before claiming one for a new subsystem. §17 is
  already "map-kind gates" while open issue #3 calls sun shafts §17, and the numbers are allocation
  order rather than document order (§14 sits between §7b and §8; §6a/§6b sit inside §7's region).
  §20d is currently claimed by in-flight PR #98 (`angstrom` branch, Ångström exponent) and is not on
  `main` — check `gh pr list` as well as `DESIGN.md` before taking a number.
- **Colour-temperature shifts interpolate in mireds (10⁶/K), not Kelvin.** A mired shift is
  approximately linear in optical depth, which is the quantity the column fractions scale. Both
  spaces agree exactly at the endpoints, so an endpoint test will not catch a regression here — only
  the interior moves. §20 records the derivation.
- **Never replace a measured scenario pin with a computed one.** The pins in `Tests/Scenarios/*.json`
  are live measurements. If the arithmetic moves, the fix is to re-run the scenario and re-measure,
  not to re-derive the number and edit it in. PR #95 deliberately left a pin at its measured
  −0.0959 rather than updating it to the predicted −0.101.
- **Survey before you pick an hour.** Outside the poles §19's ozone band is only ~0.8 h wide at
  latitude 45 and ~0.6 h at latitude 5, so an hourly scenario grid straddles it entirely and the
  capture reads "the effect is absent". Survey at ≤0.25 h with the `sun_elevation` probe, then pin
  `sun_elevation` next to the effect probe so a later clock change fails loudly instead of silently
  emptying the frames. RimWorld's clock does not put sunset at 18:00.
- **`Patch_ShadowMeshPerimeter` replaces `SectionLayer_SunShadows.Regenerate` with a Prefix**, which
  is why Perspective: Eaves is declared `incompatibleWith` rather than merely ordered — one of the
  two mods is always dead. §15 reimplements its feature natively.
