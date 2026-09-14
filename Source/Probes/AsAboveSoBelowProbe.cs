using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorldTestHarness.Mod.Probes;
using UnityEngine;
using Verse;

namespace CelestialLighting.Probes;

// Probes for the "As above, So below II" interop. See AsAboveSoBelowCompat's header for what the
// interop does and why it exists; these answer whether it is actually engaged on a live banded map,
// which no offline test can.
//
// WHY FOUR STATE PROBES AND NOT ONE. Every one of them is a state the run can be in while LOOKING
// correct, and they fail in different directions:
//
//   aasb2_present         their assembly is loaded at all. Zero means the scenario measured an
//                         ordinary map and every other number here is meaningless.
//   aasb2_banded          ABBands.Banded — did AsAboveSoBelowBand actually take.
//   aasb2_rendering_guard ABGuard.On(Rendering). Their Regenerate wraps itself in a try/catch that
//                         DISABLES this guard on any throw, permanently, for the session. When it
//                         goes off their layer stops drawing, vanilla's overlay comes back, and our
//                         compat correctly stands aside — so the frame looks plausible and the
//                         interop under test is simply not running. Pin it to 1.
//   aasb2_overlay_owned   our own AsAboveSoBelowCompat.OwnsOverlay. The two-term stand-down: it can
//                         read 0 while banded is 1, and that difference is the guard above.
//
// The payload probe is aasb2_below_overlay_alpha, which reads the mesh THEY draw.
internal static class AsAboveSoBelowProbeBinding
{
    private const string BandsTypeName = "AsAboveSoBelow.ABBands";
    private const string GuardTypeName = "AsAboveSoBelow.ABGuard";
    internal const string LayerTypeName = "AsAboveSoBelow.SectionLayer_ABBelowLighting";

    private static bool tried;
    private static Func<Map, bool> banded;
    private static Func<Map, int> bandCount;
    private static Func<bool> renderingOn;
    private static AccessTools.FieldRef<object, LayerSubMesh> layerMesh;
    private static AccessTools.FieldRef<Section, List<SectionLayer>> sectionLayers;

    internal static bool Present => Bind() && banded != null;

    internal static bool Banded(Map map) => Bind() && banded != null && map != null && banded(map);

    internal static bool RenderingOn() => Bind() && renderingOn != null && renderingOn();

    internal static int BandCount(Map map) =>
        Bind() && bandCount != null && map != null ? bandCount(map) : 0;

    // Their layer instance for the section containing a cell, or null. Walked through the Section's
    // own layer list rather than kept from a patch, so the probe reads whatever is really installed.
    internal static LayerSubMesh MeshAt(Map map, IntVec3 cell)
    {
        if (!Bind() || layerMesh == null || sectionLayers == null || map?.mapDrawer == null)
            return null;

        Section section = map.mapDrawer.SectionAt(cell);

        if (section == null)
            return null;

        List<SectionLayer> layers = sectionLayers(section);

        if (layers == null)
            return null;

        foreach (SectionLayer layer in layers)
        {
            if (layer != null && layer.GetType().FullName == LayerTypeName)
                return layerMesh(layer);
        }

        return null;
    }

    private static bool Bind()
    {
        if (tried)
            return banded != null;

        tried = true;

        Type bands = AccessTools.TypeByName(BandsTypeName);
        Type guard = AccessTools.TypeByName(GuardTypeName);
        Type layer = AccessTools.TypeByName(LayerTypeName);

        if (bands == null || guard == null || layer == null)
            return false;

        try
        {
            banded = (Func<Map, bool>)Delegate.CreateDelegate(
                typeof(Func<Map, bool>), AccessTools.Method(bands, "Banded", new[] { typeof(Map) }));

            object rendering = AccessTools.Field(guard, "Rendering")?.GetValue(null);
            renderingOn = (Func<bool>)Delegate.CreateDelegate(
                typeof(Func<bool>), rendering,
                AccessTools.Method(guard, "On", new[] { rendering.GetType() }));

            bandCount = (Func<Map, int>)Delegate.CreateDelegate(
                typeof(Func<Map, int>), AccessTools.Method(bands, "BandCount", new[] { typeof(Map) }));

            layerMesh = AccessTools.FieldRefAccess<LayerSubMesh>(layer, "mesh");
            sectionLayers = AccessTools.FieldRefAccess<Section, List<SectionLayer>>("layers");
        }
        catch (Exception e)
        {
            Log.Warning("[CelestialLighting probes] As above, So below II probe binding failed: " + e);
            banded = null;
        }

        return banded != null;
    }
}

public sealed class AsAboveSoBelowPresentProbe : IProbe
{
    public string Name => "aasb2_present";

    public float Read(Map map) => AsAboveSoBelowProbeBinding.Present ? 1f : 0f;
}

public sealed class AsAboveSoBelowBandedProbe : IProbe
{
    public string Name => "aasb2_banded";

    public float Read(Map map) => AsAboveSoBelowProbeBinding.Banded(map) ? 1f : 0f;
}

public sealed class AsAboveSoBelowRenderingGuardProbe : IProbe
{
    public string Name => "aasb2_rendering_guard";

    public float Read(Map map) => AsAboveSoBelowProbeBinding.RenderingOn() ? 1f : 0f;
}

// How many bands the map has, straight off their public ABBands.BandCount.
//
// This is what separates a map their GENERATION banded from one banded after the fact. Only
// ABBandMap.Setup can move it off 1, and on a colony the scenario just settled, their
// Patch_MapGenerator_GenerateMap is the only thing that calls Setup — so a reading of 3 (their
// default level plan: one below, one above) is their generation and nothing else.
public sealed class AsAboveSoBelowBandCountProbe : IProbe
{
    public string Name => "aasb2_band_count";

    public float Read(Map map) => AsAboveSoBelowProbeBinding.BandCount(map);
}

// The map's own z, so the band layout is visible as a shape rather than only as a count: a banded
// colony is bandCount * SlotFor(width) tall while its width is untouched, which is a signature no
// ordinary map has.
public sealed class MapHeightProbe : IProbe
{
    public string Name => "map_height";

    public float Read(Map map) => map?.Size.z ?? 0;
}

public sealed class MapWidthProbe : IProbe
{
    public string Name => "map_width";

    public float Read(Map map) => map?.Size.x ?? 0;
}

public sealed class AsAboveSoBelowOverlayOwnedProbe : IProbe
{
    public string Name => "aasb2_overlay_owned";

    public float Read(Map map) => AsAboveSoBelowCompat.OwnsOverlay(map) ? 1f : 0f;
}

// THE PAYLOAD. The alpha of the lighting-overlay vertex at a cell, read out of the mesh As above, So
// below II actually draws.
//
// This is the number the player's report was about. Indoor sky occlusion writes ALPHA — sky cover —
// into the overlay, and on a banded map the mesh it used to write was vanilla's, which is baked and
// never drawn there. A run with the feature on and one with it off must differ HERE, on their mesh,
// or the fix has not landed however green everything else looks.
//
// Reads the CENTRE vertex of the cell rather than a corner: a corner is shared by four cells and
// averages them, so an occlusion confined to one roofed cell shows at a quarter strength and reads
// like a weak effect rather than a working one. The centre index is
// (Width+1)*(Height+1) + row*Width + col, which is vanilla's own lattice — theirs is that lattice
// because they bake it with vanilla's own Bake (pinned by AsAboveSoBelowContractTests).
//
// Returns -1 when there is no such mesh, which is distinguishable from every real alpha (0..1) so a
// scenario can pin "the layer is drawing" separately from "the alpha is right". A 0 here would be
// indistinguishable from a fully transparent overlay.
public sealed class AsAboveSoBelowOverlayAlphaProbe : IProbe
{
    private readonly IntVec3 offsetFromCentre;

    public string Name { get; }

    // Offset from map centre rather than an absolute cell, matching GlowGridCellProbe: fixtures are
    // built relative to centre, so an absolute cell would silently read open ground if the fixture's
    // map size changed. Registered per-cell at construction so one scenario can pin several.
    public AsAboveSoBelowOverlayAlphaProbe(string name, IntVec3 offsetFromCentre)
    {
        Name = name;
        this.offsetFromCentre = offsetFromCentre;
    }

    public float Read(Map map)
    {
        if (map == null)
            return -1f;

        IntVec3 cell = map.Center + offsetFromCentre;

        if (!cell.InBounds(map) || map.mapDrawer == null)
            return -1f;

        LayerSubMesh subMesh = AsAboveSoBelowProbeBinding.MeshAt(map, cell);
        Mesh mesh = subMesh?.mesh;

        if (mesh == null)
            return -1f;

        Color32[] colors = mesh.colors32;

        if (colors == null)
            return -1f;

        Section section = map.mapDrawer.SectionAt(cell);

        if (section == null)
            return -1f;

        CellRect rect = new CellRect(section.botLeft.x, section.botLeft.z, Section.Size, Section.Size);
        rect.ClipInsideMap(map);

        int col = cell.x - rect.minX;
        int row = cell.z - rect.minZ;

        if (col < 0 || row < 0 || col >= rect.Width || row >= rect.Height)
            return -1f;

        int index = (rect.Width + 1) * (rect.Height + 1) + row * rect.Width + col;

        if (index < 0 || index >= colors.Length)
            return -1f;

        return colors[index].a / 255f;
    }
}
