using UnityEngine;
using Verse;

namespace CelestialLighting;

// The shared draw loop behind all three cloud lanes — §23b's underlight, §23c's shadows and §25's
// sheets. One layout, three passes.
//
// WHY THIS EXISTS. §25 moved from a tiled field to bounded sheets, and for a while the two
// illumination lanes were still keyed on the tiled field. That put two different cloud patterns on
// one screen: the shadow patches and the drawn clouds disagreed about where the clouds were, which is
// exactly the thing the "one field" design was for. This file is the fix — every lane iterates the
// same CloudSheetLayout placements, so a bright patch of ground is under a gap by construction rather
// than by two subsystems being tuned to look similar.
//
// TWO WAYS TO DRAW ONE SHEET, and which one a lane uses is decided by whether a roof stops it.
//
//   §25 (cloud)      its own quad, above FogOfWar, unmasked. Cloud is above the roof and should draw
//                    over the whole map — masking it would carve a cloud-shaped hole out of the sky
//                    wherever somebody had built a barn.
//
//   §23b/§23c        the OPEN-SKY MASK's geometry, with the blob positioned through its map-space
//   (light)          UVs. These are light reaching the GROUND, which a roof stops, and a bounded quad
//                    cannot be clipped to an arbitrary cell set in one draw call. Drawing the mask and
//                    moving the texture instead gets both properties at once: the shape is bounded,
//                    and cells the mask does not contain are simply never drawn.
//
// The UV route is what cost rotation — a texture transform can translate, scale and mirror but cannot
// rotate — see CloudSheetLayout.Placement.FlipU for why that was paid rather than kept for §25 alone.
public static class CloudSheetDraw
{
    // Reused across all three lanes and every frame: the placements are identical for a given tick, so
    // computing them once per lane would be three times the work for the same answer, and allocating a
    // fresh array per frame would be garbage on a path that runs whenever a cloud is on screen.
    private static readonly CloudSheetLayout.Placement[] Placements =
        new CloudSheetLayout.Placement[CloudSheetLayout.MaxSheets];

    private static int _placedTick = int.MinValue;
    private static int _placedMapId = -1;
    private static int _placedCount;

    // How many sheets are up over this map right now, placing them if this frame has not already.
    public static int PlaceSheets(Map map, out CloudSheetLayout.Placement[] placements)
    {
        placements = Placements;

        int ticks = Find.TickManager?.TicksAbs ?? 0;
        int mapId = map.uniqueID;

        if (ticks == _placedTick && mapId == _placedMapId)
            return _placedCount;

        int count = CloudSheetLayout.SheetCount(CloudLayers.CloudFractionFor(map));
        for (int i = 0; i < count; i++)
            Placements[i] = CloudSheetLayout.PlacementFor(i, map.Tile.tileId, ticks, map.Size.x, map.Size.z);

        _placedTick = ticks;
        _placedMapId = mapId;
        _placedCount = count;
        return count;
    }

    // Points `material` at the given sheet, for a mesh whose UVs chart the whole map — the open-sky
    // mask or vanilla's shared plane, which use the same convention.
    public static void ApplySheetUvs(
        Material material, in CloudSheetLayout.Placement placement, Map map, int atlasCells, int blob)
    {
        CloudSheetLayout.UvTransform(
            placement, map.Size.x, map.Size.z, atlasCells, blob,
            out float scaleU, out float scaleV, out float offsetU, out float offsetV);

        material.mainTextureScale = new Vector2(scaleU, scaleV);
        material.mainTextureOffset = new Vector2(offsetU, offsetV);
    }

    // Draws one sheet through the open-sky mask, i.e. onto unroofed ground only. Returns false when
    // the map has no open sky at all, which the caller reads as "stop, not just skip this sheet".
    public static bool DrawThroughMask(Map map, Material material, float altitude)
    {
        Mesh mesh = OpenSkyMask.MeshFor(map);
        if (mesh == null)
            return false;

        // The two meshes need different origins: vanilla's shared plane is centred on map.Center and
        // placed by SkyOverlay.DrawWorldOverlay, while our masked mesh is built in absolute cell
        // coordinates and draws at the origin. Branching on which came back keeps that difference in
        // one visible place — the same shape SnowGlareOverlay uses.
        if (mesh == MeshPool.wholeMapPlane)
        {
            SkyOverlay.DrawWorldOverlay(map, material, altitude);
            return true;
        }

        Graphics.DrawMesh(mesh, new Vector3(0f, altitude, 0f), Quaternion.identity, material, 0);
        return true;
    }
}
