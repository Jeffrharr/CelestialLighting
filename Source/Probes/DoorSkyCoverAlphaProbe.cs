using UnityEngine;
using Verse;
using RimWorldTestHarness.Mod.Probes;

namespace CelestialLighting.Probes;

// Excluded from the shipped CelestialLighting.dll (see the <Compile Remove> in
// CelestialLighting.csproj) and compiled into TestMod/CelestialLighting.Probes.csproj instead — the
// shipped mod must never take a hard reference on RimWorldTestHarness, a dev-only tool.
//
// WHAT THIS MEASURES. The per-door leak feature (IndoorOcclusionMath.DoorSkyLeakFor,
// Patch_IndoorSkyOcclusion) writes its result into the SAME baked vertex alpha
// Patch_IndoorSkyOcclusion's corner/centre passes already write for every other cell, so a
// screenshot of a doorway is dominated by the door's own sprite art (a security door's boxy casing
// and a glass door's pane graphic are different colours to begin with, independent of any lighting)
// rather than by the occlusion gradient underneath it — see door_leak_by_type.json's live run
// (issue tracked in conversation, not yet filed): three door types measured a few tenths of a
// CIELAB deltaE in the WRONG rank order from screenshot crops alone, because the crop nearest a
// door is exactly the crop most contaminated by that door's own artwork. This probe instead reads
// the mesh vertex CoverAlpha itself, the same way ShadowExtrusionProbe reads baked shadow alpha
// instead of screenshot pixels (see its header) — the last value the CPU owns before the GPU
// applies it, with no sprite art or antialiasing in between.
//
// WHY THE CENTRE VERTEX, NOT A CORNER. WriteCentres computes the door cell's own centre as the mean
// of its four corners (CentreOcclusion's boundary-cell branch — a door is never BlocksSky, so it
// always takes this path), and CapOcclusion has already been applied to each corner before that
// mean. Reading the centre is therefore the single number that already folds in every corner's
// door-leak/MinIndoorBrightness/AmbientLightCompat term, rather than requiring the caller to average
// four separate corner reads by hand.
//
// Lower alpha == less occluded == more sky reaches the cell (IndoorOcclusionMath.FullSkyCover's own
// doc comment: alpha 255 is a vertex that "takes none of the sky colour"). A leakier door
// (glass, blockLight false) should therefore read a LOWER alpha than a tougher opaque door
// (SecurityDoor, high MaxHitPoints) sealed into the same room.
public sealed class DoorSkyCoverAlphaProbe : IProbe
{
    // Sentinels rather than exceptions, matching ShadowExtrusionProbe's convention, so a bad scenario
    // setup reads as an obviously-wrong number in the report instead of failing the run at an
    // unrelated step.
    private const float SectionMissing = -1f;
    private const float LayerMissing = -2f;
    private const float SubMeshMissing = -3f;
    private const float VertexOutOfRange = -4f;

    private readonly IntVec3 offset;

    public string Name { get; }

    // offset: map-centre-relative, matching AmbientLightDoorCellProbe's convention — not derived from
    // the scenario at runtime (the harness hands probes a Map, not the scene plan that built it), so
    // a scenario edit that moves a door must move the matching probe's offset too.
    public DoorSkyCoverAlphaProbe(string name, IntVec3 offset)
    {
        Name = name;
        this.offset = offset;
    }

    public float Read(Map map)
    {
        IntVec3 cell = map.Center + offset;

        Section section = map.mapDrawer.SectionAt(cell);
        if (section == null)
            return SectionMissing;

        SectionLayer_LightingOverlay layer =
            section.GetLayer(typeof(SectionLayer_LightingOverlay)) as SectionLayer_LightingOverlay;
        if (layer == null)
            return LayerMissing;

        // Regenerate() is idempotent and is exactly what Section.TryUpdate calls — the same
        // reasoning ShadowExtrusionProbe's header gives for why forcing it here is safe: only
        // sections the camera has visited have a built mesh otherwise.
        layer.Regenerate();

        LayerSubMesh subMesh = layer.GetSubMesh(MatBases.LightOverlay);
        Mesh mesh = subMesh?.mesh;
        Color32[] colors = mesh?.colors32;
        if (colors == null)
            return SubMeshMissing;

        // Mirrors Patch_IndoorSkyOcclusion.Postfix's own rect/index math exactly (down to reusing
        // Section.CellRect, which applies the identical ClipInsideMap), so this reads the same
        // vertex the patch wrote rather than a re-derived approximation of it.
        CellRect rect = section.CellRect;
        int firstCenterInd = (rect.Width + 1) * (rect.Height + 1);
        int vertex = firstCenterInd + (cell.z - rect.minZ) * rect.Width + (cell.x - rect.minX);
        if (vertex < firstCenterInd || vertex >= colors.Length)
            return VertexOutOfRange;

        return colors[vertex].a / 255f;
    }
}
