using System;
using RimWorldTestHarness.Mod.Probes;
using Verse;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference to RimWorldTestHarness, a dev-only tool.
//
// Whether §27's DRAWN geometry actually reached the GPU, and at what render queue.
//
// WHY THE OTHER §27 PROBES CANNOT ANSWER THIS. Every one of them reads state that exists whether or
// not the draw runs. `vector_light_verts` counts the vertices of a mesh that was BUILT;
// `vector_light_lit_area` is the polygon's area; `vector_light_shadow_fraction` is a property of the
// coverage grid. All three read perfectly healthy while `VectorLightOverlay.Draw` returns at its
// first guard and not one pixel changes — which is not a hypothetical failure, because the
// composition flags stand that pass down ON PURPOSE and a scenario has no other way to state which
// arm it is in. It is also the exact shape of the failure §15 shipped with unit tests and a numeric
// probe both green.
//
// SO THIS READS THE CALL, NOT THE INTENT. VectorLightOverlay counts what it handed to
// Graphics.DrawMesh, per frame, and this reports it. A pin of 0 is as meaningful as a pin of 2: it
// says the pass stood down deliberately rather than that nobody looked.
//
// The queue metric earns its place separately — see VectorLightOverlay.DrawnQueue for the day it
// cost. An additive pass is order-independent only against other additive passes; the lighting
// overlay is a MULTIPLY, so a pass drawn before it is attenuated by it and one drawn after is not,
// and the symptom is a frame DIMMER than vanilla exactly where the new code adds light.
public sealed class VectorLightDrawProbe : IProbe
{
    public enum Metric
    {
        // How many emitter meshes were handed to Graphics.DrawMesh on the last Draw.
        Meshes,

        // How many triangles those meshes carried between them. Moves when the geometry changes
        // shape, where Meshes only moves when an emitter appears or the pass stands down — so the
        // pair separates "the beam changed" from "the beam stopped".
        Triangles,

        // The render queue they were drawn at. Vanilla's MoteGlow is the reference; anything else
        // means the pass has moved to the wrong side of the lighting overlay's multiply.
        Queue,
    }

    private readonly Metric metric;

    public string Name { get; }

    public VectorLightDrawProbe(string name, Metric metric)
    {
        Name = name;
        this.metric = metric;
    }

    // Takes the map for the interface's sake and does not use it: these are per-frame figures for
    // the map that was drawn, and §27 draws the current map only.
    public float Read(Map map)
    {
        switch (metric)
        {
            case Metric.Meshes:
                return VectorLightOverlay.DrawnMeshCount;
            case Metric.Triangles:
                return VectorLightOverlay.DrawnTriangleCount;
            case Metric.Queue:
                return VectorLightOverlay.DrawnQueue;
            default:
                throw new ArgumentOutOfRangeException(nameof(metric), metric, null);
        }
    }
}
