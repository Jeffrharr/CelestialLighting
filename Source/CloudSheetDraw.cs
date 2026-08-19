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

    // §25b's deck mixture. Reused for the same reason the placements are: it is one answer per map,
    // and three lanes ask for it.
    private static readonly float[] DeckWeights = new float[CloudDeckMath.DeckCount];

    // Whether the cached placements were built under §25d's layout. Part of the cache KEY, not just
    // a note: the placements are cached per tick, a harness scenario flips this flag between two
    // frames of a paused colony, and a paused colony's tick does not move — so without this the
    // second frame would silently redraw the first frame's layout and the A/B would compare a build
    // against itself while looking perfectly healthy.
    private static bool _placedPresent;

    private static int _placedTick = int.MinValue;
    private static int _placedMapId = -1;
    private static int _placedCount;
    private static int _mixtureFrame = -1;

    // How many sheets are up over this map right now, placing them if this frame has not already.
    public static int PlaceSheets(Map map, out CloudSheetLayout.Placement[] placements)
    {
        placements = Placements;

        int ticks = Find.TickManager?.TicksAbs ?? 0;
        int mapId = map.uniqueID;

        // THE MIXTURE IS RESOLVED PER FRAME, THE PLACEMENTS PER TICK, and the two cadences differ on
        // purpose. Where a sheet IS depends on nothing but the tick, so caching it on the tick is
        // exact. What KIND of cloud it is depends on the weather, and the weather can change while
        // the tick does not — most obviously on a paused colony, which is the state every harness
        // scenario is in the moment after it jumps the clock.
        //
        // That is not a harness quirk to work around, it is a correctness bug the harness found. The
        // probe reading this reports the live mixture while the tick-cached placements would still be
        // drawing the old one, so the sky on screen and the number in the report would disagree —
        // which is the exact discipline CloudLayers' header exists to keep. It showed up as a §25b
        // A/B measuring ΔE 0.00 with every pixel unchanged: the feature toggle could not reach the
        // draw at all, because the game was paused and the cache never expired.
        bool mixtureMoved = ResolveMixture(map, mapId);

        bool present = CelestialLightingFeatures.CloudPresence;

        if (ticks == _placedTick && mapId == _placedMapId && !mixtureMoved
            && present == _placedPresent)
        {
            return _placedCount;
        }

        // §25d's layout: many small clouds rather than a few map-sized ones. Both the cap and the
        // size move together — see CloudSheetLayout.PresentSizeFraction for the difference map that
        // says a five-sheet sky of two-thirds-map sheets can only ever render as one flat wash.
        //
        // Chosen HERE rather than per lane on purpose: §23b's underlight, §23c's shadows and §25's
        // sheets all read these same placements, and that shared layout is the whole reason a bright
        // patch of ground sits under a gap by construction. A flag that moved the clouds for one lane
        // and not the others would put the sky and the ground back to disagreeing about where the
        // cloud is, which is the failure this file exists to have fixed.
        int cap = present ? CloudSheetLayout.PresentSheetCap : CloudSheetLayout.ShippedSheetCap;
        float sizeFraction = present
            ? CloudSheetLayout.PresentSizeFraction
            : CloudSheetLayout.BaseSizeFraction;

        int count = CloudSheetLayout.SheetCount(CloudLayers.CloudFractionFor(map), cap);
        for (int i = 0; i < count; i++)
        {
            Placements[i] = CloudSheetLayout.PlacementFor(
                i, map.Tile.tileId, ticks, map.Size.x, map.Size.z, DeckWeights, sizeFraction);
        }

        _placedTick = ticks;
        _placedMapId = mapId;
        _placedCount = count;
        _placedPresent = present;
        return count;
    }

    // Rewrites DeckWeights for this frame and reports whether it moved.
    //
    // ONCE PER FRAME RATHER THAN PER CALL, because all three lanes ask and they ask in the same
    // frame. The read behind it (WeatherDimming.CloudAltitudeMetresFor) walks the weather pair,
    // resolves a mod extension on each and lerps them, which is small but not free, and doing it
    // three times over would be paying twice for an answer that cannot have changed in between.
    //
    // Keyed on Time.frameCount rather than on the tick precisely because the tick is the thing that
    // can stand still while this changes — see PlaceSheets. Also keyed on the map, so switching
    // between two colonies inside one frame cannot hand the second one the first one's sky.
    private static bool ResolveMixture(Map map, int mapId)
    {
        int frame = Time.frameCount;
        if (frame == _mixtureFrame && mapId == _placedMapId)
            return false;

        _mixtureFrame = frame;

        float first = DeckWeights[0];
        float last = DeckWeights[CloudDeckMath.DeckCount - 1];

        // §25b: which decks this sky's clouds are on, decomposed from the single deck altitude §13's
        // classifier assigns the weather.
        //
        // Flag off collapses it to the low deck, which is the single-deck sky §25 drew before §25b —
        // same atlas, same shapes, same placements, same speeds, so an A/B of the flag isolates the
        // varieties themselves rather than measuring the whole sheet lane. See
        // CelestialLightingFeatures.CloudDeckVarieties for why that shape of "off" is the point.
        if (CelestialLightingFeatures.CloudDeckVarieties)
            CloudDeckMath.MixtureFor(DeckWeights, WeatherDimming.CloudAltitudeMetresFor(map));
        else
            CloudDeckMath.SingleDeckMixture(DeckWeights);

        // Comparing the two END weights rather than all three is enough and is not a shortcut: the
        // mixture is a normalised distribution interpolated along one monotone axis, so nothing can
        // move the middle without moving at least one end. Exact float equality is what is wanted
        // here — the question is "is this the same array I placed from", not "is it close".
        return DeckWeights[0] != first || DeckWeights[CloudDeckMath.DeckCount - 1] != last;
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
