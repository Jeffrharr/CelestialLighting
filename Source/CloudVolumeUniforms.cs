using System;

namespace CelestialLighting;

// §28: the pure half of what CloudVolumeShader.Configure sets on a sheet's material every frame.
//
// WHY THIS FILE EXISTS AT ALL, given that the arithmetic in it was already correct where it sat.
// Configure made seven native `Material.Set*` calls per sheet per frame — 280 of them at
// CloudSheetLayout.MaxSheets — and measured ~31 us a frame, the largest single item left in §28's
// sweep. Nearly all of those writes rewrote a value byte-for-byte identical to the one already in
// the material, because the sheet's slot keeps its material for the whole session and most of what
// Configure sets does not change between frames.
//
// Skipping a write needs a record of what was last written, and a record is only safe if the thing
// it records genuinely depends on nothing else. That is a claim about the arithmetic, not about the
// adapter, so the arithmetic is what got pulled out here: `Geometry` is every uniform derived from
// the sheet's PLACEMENT and its DECK, and the offline tests pin that it moves when those move and
// does not move when the sun or the light does. The cache in Configure is correct exactly to the
// extent that claim holds, and now the claim is testable without booting the game.
//
// THREE CADENCES, and the split is the whole design:
//
//   per crossing  Geometry — atlas cell, mirroring, cell bounds, deck thickness, march
//                 coefficients. A sheet is re-placed roughly once a crossing (CloudSheetDraw
//                 caches placements per TICK), so in the steady state this is written once and
//                 then skipped for hundreds of frames.
//   per frame     the sun direction, which moves continuously, and the lit/shadow colours, which
//                 follow the sky. Cached anyway — a struct compare is a handful of float
//                 comparisons against a managed-to-native call — but expected to miss.
//   never         the atlas size and the padded slice count, build constants that ride along
//                 inside Geometry because the writer needs them in the same vector.
//
// No UnityEngine or Verse usings, per the house rule: primitives in, a plain struct out.
public static class CloudVolumeUniforms
{
    // Every uniform value a sheet's placement and deck decide, in the groupings the four shader
    // properties want them in.
    //
    // A STRUCT WITH VALUE EQUALITY, and it has to be exact rather than tolerant. A near-equal
    // comparison would let a slowly drifting value never trigger a write and drift arbitrarily far
    // from what the shader should be sampling; these are all derived from integers and constants,
    // so exact equality is the honest test and it either matches or it genuinely changed.
    public readonly struct Geometry : IEquatable<Geometry>
    {
        // _Volume_ST — which atlas cell this sheet wears, and which way round.
        public readonly float ScaleU;
        public readonly float ScaleV;
        public readonly float OffsetU;
        public readonly float OffsetV;

        // _VolumeParams — atlas size, padded slice count, and this deck's own vertical extent.
        public readonly float AtlasSize;
        public readonly float PaddedLayers;
        public readonly float PeakTexels;
        public readonly float LayerTexels;

        // _CellBounds — the inclusive texel rectangle of this sheet's blob, which is what stops a
        // march wandering into the neighbouring cloud.
        public readonly float MinX;
        public readonly float MinY;
        public readonly float MaxX;
        public readonly float MaxY;

        // _MarchParams — view extinction, the ambient wrap floor, the shadow reach in texels, and
        // the light ray's own coefficient.
        public readonly float ViewExtinction;
        public readonly float AmbientWrap;
        public readonly float ShadowReach;
        public readonly float LightExtinction;

        public Geometry(
            float scaleU, float scaleV, float offsetU, float offsetV,
            float atlasSize, float paddedLayers, float peakTexels, float layerTexels,
            float minX, float minY, float maxX, float maxY,
            float viewExtinction, float ambientWrap, float shadowReach, float lightExtinction)
        {
            ScaleU = scaleU;
            ScaleV = scaleV;
            OffsetU = offsetU;
            OffsetV = offsetV;
            AtlasSize = atlasSize;
            PaddedLayers = paddedLayers;
            PeakTexels = peakTexels;
            LayerTexels = layerTexels;
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
            ViewExtinction = viewExtinction;
            AmbientWrap = ambientWrap;
            ShadowReach = shadowReach;
            LightExtinction = lightExtinction;
        }

        public bool Equals(Geometry other) =>
            ScaleU == other.ScaleU && ScaleV == other.ScaleV
            && OffsetU == other.OffsetU && OffsetV == other.OffsetV
            && AtlasSize == other.AtlasSize && PaddedLayers == other.PaddedLayers
            && PeakTexels == other.PeakTexels && LayerTexels == other.LayerTexels
            && MinX == other.MinX && MinY == other.MinY
            && MaxX == other.MaxX && MaxY == other.MaxY
            && ViewExtinction == other.ViewExtinction && AmbientWrap == other.AmbientWrap
            && ShadowReach == other.ShadowReach && LightExtinction == other.LightExtinction;

        // NO `Equals(object)` OVERRIDE, deliberately. The cache compares through IEquatable and
        // never boxes, and an override would have to be annotated `object?` to satisfy the test
        // project's nullable context while the shipped net481 build has that context off — one of
        // the two would warn whichever way it was written. ValueType's default still answers a boxed
        // comparison correctly, just slowly, and nothing on a frame path takes that route.
        //
        // GetHashCode is overridden anyway: it is never a dictionary key today — the cache is a flat
        // array indexed by slot — but a hash derived from a subset of fields stays consistent with
        // that default equality, and the day somebody does put one in a set it will not silently
        // scatter equal values across buckets.
        public override int GetHashCode()
        {
            int hash = ScaleU.GetHashCode();
            hash = (hash * 397) ^ OffsetU.GetHashCode();
            hash = (hash * 397) ^ PeakTexels.GetHashCode();
            hash = (hash * 397) ^ MinX.GetHashCode();
            hash = (hash * 397) ^ MinY.GetHashCode();
            return hash;
        }
    }

    // Everything the shader needs about a sheet that is decided by where the sheet was placed and
    // which deck it sits on. Deliberately takes no sun and no colour: that absence is the property
    // the cache depends on, and the signature is the cheapest place to state it.
    //
    // `basePeakTexels` is deck 0's peak, the reference the two extinction coefficients are relative
    // to — see CloudRaymarchMath.ViewExtinctionFor.
    public static Geometry GeometryFor(
        int blob, int atlasCells, int atlasSize, bool flipU, bool flipV,
        float peakTexels, float basePeakTexels, int volumeLayers, int padSlices)
    {
        int blobSize = atlasCells > 0 ? atlasSize / atlasCells : atlasSize;
        int blobX = atlasCells > 0 ? blob % atlasCells : 0;
        int blobY = atlasCells > 0 ? blob / atlasCells : 0;

        float cell = atlasCells > 0 ? 1f / atlasCells : 1f;

        return new Geometry(
            scaleU: flipU ? -cell : cell,
            scaleV: flipV ? -cell : cell,
            offsetU: (blobX + (flipU ? 1 : 0)) * cell,
            offsetV: (blobY + (flipV ? 1 : 0)) * cell,
            atlasSize: atlasSize,
            paddedLayers: volumeLayers + padSlices,
            peakTexels: peakTexels,
            layerTexels: volumeLayers > 0 ? peakTexels / volumeLayers : 0f,
            minX: blobX * blobSize,
            minY: blobY * blobSize,
            maxX: blobX * blobSize + blobSize - 1,
            maxY: blobY * blobSize + blobSize - 1,
            viewExtinction: CloudRaymarchMath.ViewExtinctionFor(peakTexels, basePeakTexels),
            ambientWrap: CloudRaymarchMath.AmbientWrap,
            shadowReach: blobSize * 0.667f,
            lightExtinction: CloudRaymarchMath.LightExtinctionFor(peakTexels, basePeakTexels));
    }

    // The sun direction as this sheet sees it: the map's sun vector, mirrored to match whichever way
    // the sheet's texture was flipped.
    //
    // MIRRORED WITH THE TEXTURE — a sheet drawn with a negative texture scale reads the atlas
    // backwards, so an unmirrored light lands a baked lit side on the wrong flank. This moved out of
    // Configure with the rest, but its cadence is the OPPOSITE of Geometry's: the flips change once
    // a crossing and the sun changes every frame, so the combination changes every frame and there
    // is nothing here to skip. It is separated from Geometry precisely so that it cannot force
    // Geometry's thirteen values to be rewritten alongside it.
    public static void SunDirection(
        float lu, float lv, float lh, bool flipU, bool flipV,
        out float u, out float v, out float h)
    {
        u = flipU ? -lu : lu;
        v = flipV ? -lv : lv;
        h = lh;
    }
}
