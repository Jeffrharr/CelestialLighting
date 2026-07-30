using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Reads the two map-kind gates back out of a running game, through the exact same MapSky adapter the
// patches call, so a scenario pins the DECISION rather than its downstream visual consequences.
//
// Why these are worth their own probes rather than being inferred from the effect probes. Every
// gated effect reads zero for at least one other reason — twilight is zero at noon, aurora is zero
// without a flare, weather dimming is zero under Clear — so a scenario asserting "civil_twilight == 0
// on a cavern" cannot distinguish "the gate fired" from "it was the wrong time of day anyway". These
// two answer the question directly, and the effect probes then confirm the consequence.
//
// Both report 1/0 rather than a bool because the harness's Probe step compares floats.

// Whether a ceiling stands between this map and the sky — the gate the sky-sourced effects use.
// 1 on a Biomes! Caverns cavern or vanilla's undercave, 0 on open air AND on orbit.
public sealed class MapEnclosedProbe : IProbe
{
    public string Name => "map_enclosed";

    public float Read(Map map) => MapSky.IsEnclosed(map) ? 1f : 0f;
}

// Whether this map draws shadows at all — Gate B, vanilla's own disableShadows flag.
//
// Kept separate from map_enclosed specifically so a scenario can pin the pair on orbit and catch the
// two gates being collapsed into one: orbit must read map_enclosed 0 and map_draws_shadows 1, and any
// change that makes it read 1/0 has quietly stripped orbit of sky effects and its shadows with it.
public sealed class MapDrawsShadowsProbe : IProbe
{
    public string Name => "map_draws_shadows";

    public float Read(Map map) => MapSky.DrawsShadows(map) ? 1f : 0f;
}

// Whether the sky over this map is opaque RIGHT NOW — the dynamic gate (issue #35).
//
// The reason this one needs a probe even more than the other two do: it is the only map-kind gate that
// is not a function of map.Biome, so a scenario cannot infer it from the biome it selected. It reads 0
// on a bare Glowforest (the harness's SetBiome does not run biomeMapConditions) and 1 only once a
// StartCondition step has actually registered DarkenedSkies, which is precisely the distinction a
// scenario has to be able to assert to prove the gate is condition-driven rather than biome-driven.
//
// It must also read 0 under an Eclipse. That carve-out is invisible in every downstream effect probe —
// an eclipse and a smoke vent both leave the sky near black — so this is the only place a scenario can
// catch §10/§10a being switched off by their own condition class.
public sealed class MapSkyBlackedOutProbe : IProbe
{
    public string Name => "map_sky_blacked_out";

    public float Read(Map map) => MapSky.SkyBlackedOut(map) ? 1f : 0f;
}
