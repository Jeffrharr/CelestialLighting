using RimWorld;
using UnityEngine;
using Verse;

namespace CelestialLighting;

// §9's per-cell desaturation, as a map draw layer of our own.
//
// Verse.Section's constructor instantiates every non-abstract SectionLayer subclass it can find
// (typeof(SectionLayer).AllSubclassesNonAbstract()), so this needs no patch to be drawn — declaring
// the class is the registration. It is regenerated on the same signal vanilla's own lighting overlay
// uses (MapMeshFlagDefOf.GroundGlow), because it answers a question about the same grid.
//
// WHY A WHOLE LAYER. See NightDesaturationMath's header for the two channels that cannot express
// this: the camera saturation is global (it greys campfires), and the sky tint multiplies (it cannot
// desaturate at all). Alpha compositing toward a grey IS lerp(colour, grey, t) — the desaturation
// operation — and a mesh can carry a different alpha per vertex. That is the whole trick, and it
// needs no replacement shader for MatBases.LightOverlay, which was the reason the per-cell version
// was previously judged impossible: it is a new mesh alongside vanilla's, not a hijack of one.
//
// ALTITUDE. AltitudeLayer.Weather, which is BELOW AltitudeLayer.LightingOverlay. So the wash lands on
// the scene first and vanilla's night multiply darkens the result afterwards, rather than sitting on
// top of the darkness as a grey haze. It is above every thing/pawn altitude, so an item lying on
// unlit ground desaturates with the ground it lies on.
public class SectionLayer_NightDesaturation : SectionLayer
{
    // Only the feature flag. "How dark is it" belongs to the material's alpha, not here: at zero
    // alpha the mesh is invisible anyway, and hiding the layer per-frame on a brightness test would
    // make the layer's visibility flicker across dusk for no gain.
    public override bool Visible => CelestialLightingFeatures.LowLightDesaturation;

    public SectionLayer_NightDesaturation(Section section)
        : base(section)
    {
        // Same signal as SectionLayer_LightingOverlay: light changes move the wash, and roof changes
        // move the light. Anything else that dirties a section leaves these alphas valid.
        relevantChangeTypes = (ulong)MapMeshFlagDefOf.Roofs | (ulong)MapMeshFlagDefOf.GroundGlow;
    }

    public override void Regenerate()
    {
        LayerSubMesh subMesh = GetSubMesh(NightDesaturationOverlay.Material);
        if (subMesh.mesh.vertexCount == 0)
            SectionLayerGeometryMaker_Solid.MakeBaseGeometry(section, subMesh, AltitudeLayer.Weather);

        subMesh.Clear(MeshParts.Colors);

        CellRect rect = section.CellRect;
        for (int x = rect.minX; x <= rect.maxX; x++)
        {
            for (int z = rect.minZ; z <= rect.maxZ; z++)
            {
                AddCellColors(subMesh, x, z);
            }
        }

        subMesh.disabled = false;
        subMesh.FinalizeMesh(MeshParts.Colors);
    }

    // The nine vertices SectionLayerGeometryMaker_Solid emits per cell, in its order: four corners,
    // four edge midpoints, then the centre. Each is averaged over the cells it actually touches — a
    // corner belongs to four cells, an edge to two, the centre to one — which is what makes the wash
    // fade smoothly across cell boundaries instead of drawing a visible grid of squares. Vanilla's
    // lighting overlay does the same averaging for the same reason.
    private void AddCellColors(LayerSubMesh subMesh, int x, int z)
    {
        float here = WashAt(x, z);
        float west = WashAt(x - 1, z);
        float east = WashAt(x + 1, z);
        float south = WashAt(x, z - 1);
        float north = WashAt(x, z + 1);

        byte bottomLeft = ToAlpha((here + west + south + WashAt(x - 1, z - 1)) * 0.25f);
        byte topLeft = ToAlpha((here + west + north + WashAt(x - 1, z + 1)) * 0.25f);
        byte topRight = ToAlpha((here + east + north + WashAt(x + 1, z + 1)) * 0.25f);
        byte bottomRight = ToAlpha((here + east + south + WashAt(x + 1, z - 1)) * 0.25f);

        byte left = ToAlpha((here + west) * 0.5f);
        byte top = ToAlpha((here + north) * 0.5f);
        byte right = ToAlpha((here + east) * 0.5f);
        byte bottom = ToAlpha((here + south) * 0.5f);

        subMesh.colors.Add(WashColor(bottomLeft));
        subMesh.colors.Add(WashColor(left));
        subMesh.colors.Add(WashColor(topLeft));
        subMesh.colors.Add(WashColor(top));
        subMesh.colors.Add(WashColor(topRight));
        subMesh.colors.Add(WashColor(right));
        subMesh.colors.Add(WashColor(bottomRight));
        subMesh.colors.Add(WashColor(bottom));
        subMesh.colors.Add(WashColor(ToAlpha(here)));
    }

    // Local light only — ignoreSky: true. The sky's own contribution is already the whole of
    // PurkinjeMath's factor, which scales this layer through the material's alpha; counting it again
    // per cell would both double it and, worse, invert the intent, since a brightening sky would
    // start exempting the outdoor cells the effect is for.
    //
    // Out-of-bounds neighbours (the map edge) read as unlit rather than as an exemption, so the wash
    // runs cleanly off the edge instead of fading out along it.
    private float WashAt(int x, int z)
    {
        IntVec3 cell = new IntVec3(x, 0, z);
        if (!cell.InBounds(base.Map))
            return 1f;

        return NightDesaturationMath.CellWash(
            base.Map.glowGrid.GroundGlowAt(cell, ignoreCavePlants: false, ignoreSky: true));
    }

    // White RGB: the wash's colour comes from the material (a dark grey), so the vertex carries only
    // "how much of it applies here". The shader multiplies the two.
    private static Color32 WashColor(byte alpha) => new Color32(byte.MaxValue, byte.MaxValue, byte.MaxValue, alpha);

    private static byte ToAlpha(float wash) => (byte)Mathf.Clamp(Mathf.RoundToInt(wash * 255f), 0, 255);
}
