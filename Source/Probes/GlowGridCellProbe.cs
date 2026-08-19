using System;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Vanilla's GAMEPLAY light at one cell: Verse.GlowGrid.GroundGlowAt, the number plant growth, work
// speed, pawn vision and every other mod read.
//
// WHY §27e NEEDS ITS OWN PROBE FOR THIS. Every other vector_light_* probe reads our polygon, and the
// polygon is exactly what both of §27e's arms have in common — turn on vector_light_open_doors and
// the beam is drawn whether or not vanilla agrees, so lit_area cannot tell the two arms apart. This
// is the one number that can: with the drawn-only flag it must NOT move (that is the whole claim,
// that we changed only what is rendered), and with vector_light_door_glow_blocker it must.
//
// A probe that must stay STILL is as load-bearing here as one that must move. Without it, "gameplay
// light is untouched" is a comment in CelestialLightingFeatures rather than something a run checks,
// and it is precisely the kind of claim that quietly stops being true.
//
// Offset from map centre rather than an absolute cell, matching SkyCoverVertexProbe: fixtures are
// built relative to centre, so an absolute cell would silently read open ground if the fixture's map
// size ever changed. Registered per-cell at construction, so one scenario can pin several.
public sealed class GlowGridCellProbe : IProbe
{
    private readonly IntVec3 offsetFromCentre;

    public string Name { get; }

    public GlowGridCellProbe(string name, IntVec3 offsetFromCentre)
    {
        Name = name;
        this.offsetFromCentre = offsetFromCentre;
    }

    public float Read(Map map)
    {
        if (map == null)
        {
            return 0f;
        }

        IntVec3 cell = map.Center + offsetFromCentre;

        // Out-of-bounds reads zero rather than throwing: a scenario that moves its fixture should
        // fail on a pin that went to zero, which names the problem, rather than on an exception
        // inside a probe, which names this file.
        if (!cell.InBounds(map))
        {
            return 0f;
        }

        return map.glowGrid.GroundGlowAt(cell);
    }
}
