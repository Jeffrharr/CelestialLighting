namespace CelestialLighting;

// Pure math only — no UnityEngine or Verse types anywhere in this file, same discipline as
// IndoorOcclusionMath. Compiled into both Source (net481, runs inside RimWorld) and Tests (net8.0,
// runs standalone via `dotnet test`) via a linked <Compile Include>.
//
// Subsystem 7c pure core (DESIGN.md §7c): native under-roof sky falloff, no Ambient Light
// (f1995.ambientlight) dependency. §7b's own occlusion is a blanket "every interior cell is fully
// dark" — this is the gradient near an opening that §7a's Ambient Light compat (§7b's third
// CapOcclusion argument) already gives players who have that mod installed, built natively for
// players who don't.
//
// Deliberately MIRRORS IndoorOcclusionMath.AmbientLightUnderRoofSkyLight / AmbientLightFalloffNoSky
// in shape — curSkyGlow * passThrough01 * (1 - depth/maxDepth), zero at depth <= 0 — rather than
// sharing code with it. The two formulas are conceptually independent (this one answers "how much sky
// should WE let leak in, absent any third-party mod", not "what does Ambient Light's own private
// formula compute"), and forcing one to call the other would make a deliberate future change to either
// read as breaking the other. See SkyFalloffSource for why the two sources are mutually exclusive per
// cell rather than composed — different mods, different maxDepth values by construction, and Max()-ing
// two independently-tuned gradients would put a visible seam wherever the smaller maxDepth runs out.
public static class NativeSkyFalloffMath
{
    // maxDepth lands in the same register as Ambient Light's own shipped default (12, confirmed by
    // decompiling AmbientLightFalloff.dll's AmbientLightSettings this session), so a player who tries
    // the game with and without Ambient Light installed sees a similar CHARACTER of falloff near a
    // doorway. passThroughPercent does NOT match Ambient Light's own 55f default: live playtesting on
    // the shipped 55f found a typical roofed room reading as lit up rather than gently graded near the
    // door (see NativeSkyFalloffSettings for the two matching sliders this drove) — 25f is the new
    // out-of-box "starting brightness right at the opening" that reads as a gradient, not a room light.
    public const int DefaultMaxDepth = 12;
    public const float DefaultPassThroughPercent = 25f;

    // depth: BFS distance from the nearest unroofed/non-blocking cell (NativeSkyFalloffGrid.DepthAt),
    // 0 or less meaning "not reached" — either the cell is unroofed outright (nothing to compute; the
    // ordinary vanilla sky cover already applies) or it sits further than maxDepth from any opening.
    // curSkyGlow: SkyManager.CurSkyGlow, the SAME sky term §7/§7a already floor at night, so a
    // genuinely pitch-black night still yields a near-zero fraction here even one step from a door —
    // there is nothing to redistribute once the source itself is dark, matching Ambient Light's own
    // stated design intent ("dynamically lightens based on outdoor sky brightness").
    public static float FractionAt(int depth, float curSkyGlow, int maxDepth, float passThroughPercent)
    {
        if (curSkyGlow <= 0.001f)
            return 0f;

        return Clamp01(curSkyGlow * FalloffNoSky(depth, maxDepth, passThroughPercent));
    }

    private static float FalloffNoSky(int depth, int maxDepth, float passThroughPercent)
    {
        if (depth <= 0)
            return 0f;

        int clampedMaxDepth = maxDepth < 1 ? 1 : maxDepth;
        float passThrough01 = Clamp01(passThroughPercent / 100f);
        float depthFraction = Clamp01((float)depth / clampedMaxDepth);
        return Clamp01(passThrough01 * (1f - depthFraction));
    }

    private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
}
